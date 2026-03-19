# Performance — Real-Time Chat System

---

## Current Bottlenecks

### Bottleneck 1: WebSocket connection limit per process
A single .NET 8 process can maintain ~50,000–100,000 WebSocket connections before
file descriptor limits and memory pressure become constraints. At 100,000 concurrent
users, this requires 2+ gateway instances.

### Bottleneck 2: Redis pub/sub fan-out for large groups
A message in a 500-member group generates 500 individual WebSocket pushes across
potentially many gateway instances. The Redis publish succeeds in <1ms; the fan-out
to 500 connections is CPU-bound work on the receiving gateways.

### Bottleneck 3: Message table growth
At 10,000 messages/sec, the `messages` table grows by ~864M rows/day.
Cursor pagination is O(log N) with the index, but the index itself grows and
eventually requires partitioning.

---

## Cache Hit Rate Targets

| Key                    | Target  | Notes                                  |
|------------------------|---------|----------------------------------------|
| `presence:{userId}`    | N/A     | Written on connect; TTL is the control |
| `unread:{u}:{conv}`    | ≥ 70%   | Invalidated on read; 60s TTL           |

---

## Database Read Replica Routing

| Operation                            | Target       | Reason                          |
|--------------------------------------|--------------|----------------------------------|
| `GET /conversations/{id}/messages`   | Read replica | Historical; eventual OK          |
| `GET /users/{id}/conversations`      | Read replica | List view; eventual OK           |
| `INSERT INTO messages`               | **Primary**  | Write — must be primary          |
| `UPDATE conversation_members`        | **Primary**  | Read cursor update               |
| Member check (is sender a member?)   | **Primary**  | Must reflect latest membership   |

---

## Connection Pool Sizing

| Setting               | Value | Rationale                                        |
|-----------------------|-------|--------------------------------------------------|
| Max pool per instance | 25    | High message rate; each insert is ~5ms           |
| WebSocket connections | 50,000/instance | Limit before scale-out trigger       |
| PgBouncer mode        | Transaction | Required for Dapper compatibility          |

---

## Query Performance Targets

| Query                                          | Target p95 | Index Used                      |
|------------------------------------------------|-----------|----------------------------------|
| `SELECT messages WHERE conversation_id ORDER BY id DESC` | < 10ms | `messages_conv_id_idx` |
| `SELECT conversation_members WHERE user_id`    | < 2ms     | `conv_members_user_id_idx`      |
| `INSERT INTO messages`                         | < 5ms     | Sequential                      |
| `UPDATE conversation_members last_read`        | < 3ms     | Composite PK                    |

---

## Rate Limiting Configuration

| Policy              | Limit | Window  | Endpoint                              |
|---------------------|-------|---------|---------------------------------------|
| message-send        | 60    | 1 min   | `POST /conversations/{id}/messages`   |
| conversation-create | 10    | 1 min   | `POST /conversations`                 |
| authenticated       | 120   | 1 min   | All other authenticated endpoints     |

---

# Scaling Strategy — Real-Time Chat System

## Horizontal Scaling Table

| Component               | Scales Horizontally? | Notes                                              |
|-------------------------|---------------------|----------------------------------------------------|
| WebSocket Gateway       | ✅ Partially         | Stateful connections; Redis pub/sub bridges instances |
| REST API                | ✅ Yes               | Stateless; round-robin LB                          |
| MessageService          | ✅ Yes               | All state in DB + Redis                            |
| Redis (pub/sub)         | ✅ Yes (Cluster)     | Pub/sub channels shard with keyspace               |
| PostgreSQL primary      | ❌ No (writes)       | Single write primary                               |
| PostgreSQL replicas     | ✅ Yes               | Read history queries                               |

## Load Balancing — WebSocket Requires Special Handling

```
WebSocket connections:
  Algorithm:  IP-Hash (affinity — client stays on same gateway)
  Why:        WebSocket connections are stateful; a reconnect is acceptable
              but mid-session rerouting breaks the connection
  Fallback:   On gateway failure, client reconnects to any healthy instance
              and recovers missed messages via cursor query

REST API connections:
  Algorithm:  Round-Robin (fully stateless)
  Affinity:   None
```

## Stateless Design Guarantees

1. **No in-process message buffer.** Messages are persisted to PostgreSQL before
   pub/sub publish. A gateway restart loses no messages.

2. **Redis pub/sub bridges gateways.** A message published by a client connected
   to Gateway-1 reaches a recipient connected to Gateway-2 within <5ms via the
   shared Redis channel.

3. **Reconnect recovery is self-contained.** Clients carry the last-seen message
   ID. Any gateway instance can serve the backfill query — no state coordination.

## Scaling Triggers

| Metric                           | Threshold         | Action                          |
|----------------------------------|-------------------|---------------------------------|
| WebSocket connections per instance | > 40,000        | Add WebSocket gateway instance  |
| Message insert p99               | > 20ms            | Add PostgreSQL instance         |
| Redis pub/sub latency            | > 20ms            | Add Redis Cluster node          |
