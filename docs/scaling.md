# Scaling Strategy — Real-Time Chat System

---

## Current Single-Node Bottlenecks

- **WebSocket connection limit per process**: A single .NET process on a modern server can
  maintain 50,000–100,000 concurrent WebSocket connections before memory and file descriptor
  limits become constraints. At 100,000 concurrent users, this means a minimum of 1–2 gateway
  instances are required, plus headroom for spikes.

- **Redis pub/sub throughput**: A single Redis instance handles pub/sub efficiently, but at
  10,000 messages/sec with wide fan-out (large group conversations), the number of message
  deliveries per second can multiply. A message in a 500-member group generates 500 individual
  pushes across subscribed gateways. At 10K messages/sec with average group size of 20, that
  is 200,000 push operations per second through Redis.

- **PostgreSQL message write throughput**: At 10,000 messages/sec, that is 10,000 `INSERT`
  operations per second against the `messages` table. For a single PostgreSQL primary with
  NVMe storage, this is achievable, but it is the first component to reach saturation.

- **Fan-out for large group conversations**: A single message in a very large group (100+
  members) requires the gateway layer to push to potentially many connections across many
  instances. This is CPU-bound work on the gateway.

---

## Horizontal Scaling Plan

### WebSocket Gateway

The WebSocket Gateway scales horizontally. Because Redis pub/sub bridges all instances, there
is no coordination required between gateway instances — each subscribes to the channels for
its connected users and pushes independently.

The load balancer uses IP-hash affinity to keep a client on the same gateway instance for
the duration of their session. This is a soft affinity: if an instance fails, the client
reconnects to a different instance and re-establishes presence. The new instance subscribes
to the same Redis channels and resumes delivery seamlessly.

Target: 1 gateway instance per 30,000–50,000 concurrent connections.

### REST API

The REST API is fully stateless. Scale horizontally with round-robin load balancing. No special
considerations required.

### Redis — Pub/Sub

A single Redis instance handles the pub/sub workload effectively at the defined scale. The
concern is not memory (pub/sub does not store data) but CPU for message serialisation and
network throughput for delivery.

If a single Redis instance becomes a bottleneck, partition conversations across multiple Redis
instances by conversation ID hash. Each gateway instance connects to all Redis instances and
subscribes selectively. This is complex to implement and should only be introduced when metrics
indicate actual saturation.

### Redis — Presence Store

The presence store is a simple key-value workload: SET with TTL on connect/heartbeat, DEL on
disconnect, GET on presence check. A single Redis instance handles millions of these operations
per second without issue. No scaling intervention is needed at the defined scale.

### PostgreSQL — Messages

**Phase 1 — Read replicas**: Route all message history reads (`GET /conversations/{id}/messages`)
to a read replica. Writes (new messages) go to the primary only.

**Phase 2 — Time-based table partitioning**: Partition the `messages` table by month.
Messages older than 90 days can be moved to cold storage or an archive partition.
The active partition contains only recent messages and remains small and fast.

**Phase 3 — Write batching**: If message insert throughput saturates the primary, batch
inserts from the application layer: accumulate messages in a short buffer (5–10ms) and
insert in batches rather than one-at-a-time. This is an application-layer optimization that
increases insert efficiency at the cost of slight delivery latency.

---

## Cache and Delivery Targets

| Component              | Metric                     | Target              | Notes                                        |
|------------------------|----------------------------|---------------------|----------------------------------------------|
| Redis pub/sub          | Message delivery latency   | ≤ 10ms p99          | From publish to subscriber receipt           |
| Presence TTL           | 35 seconds                 | Heartbeat every 30s | Accounts for network jitter on heartbeat     |
| Message history cache  | Not cached                 | N/A                 | History served from PostgreSQL read replica  |
| WebSocket per-instance | 50,000 connections max     | Alert at 40,000     | Scale out before limit is hit                |

---

## Connection Count Scaling Trigger

Monitor `active_websocket_connections` per instance. When any instance exceeds 40,000
connections, trigger auto-scaling to add a new gateway instance. The load balancer begins
routing new connection upgrades to the new instance immediately. Existing connections
remain on their current instance until natural reconnection events redistribute them.

Message delivery latency (end-to-end, from `POST /messages` to client WebSocket push)
should be monitored as a percentile metric. If p95 exceeds 200ms, investigate in this
order: Redis pub/sub latency → PostgreSQL write latency → WebSocket push queue depth per
gateway instance.
