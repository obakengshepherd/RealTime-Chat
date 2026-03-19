# Scaling Strategy — Real-Time Chat System

---

## Horizontal Scaling Table

| Component               | Scales Horizontally? | Notes                                                     |
|-------------------------|---------------------|-----------------------------------------------------------|
| WebSocket Gateway       | ✅ Partially         | Connections are stateful; Redis pub/sub bridges instances  |
| REST API                | ✅ Yes               | Stateless; round-robin load balancing                     |
| MessageService          | ✅ Yes               | Stateless; all state in DB + Redis                        |
| ConversationService     | ✅ Yes               | Stateless; DB-backed                                      |
| PresenceService         | ✅ Yes               | Redis is the shared state; fully stateless service        |
| Redis (pub/sub)         | ✅ Yes (Cluster)     | Pub/sub channels distributed across cluster nodes        |
| Redis (presence)        | ✅ Yes (Cluster)     | Presence keys shard by user_id hash                      |
| PostgreSQL primary      | ❌ No (writes)       | Single write primary; message inserts are high-throughput |
| PostgreSQL replicas     | ✅ Yes               | Message history reads; conversation list                  |

---

## Load Balancing — Two Different Algorithms Required

The Chat system needs different load balancing for its two traffic types:

### WebSocket Connections (Gateway instances) — IP Hash Affinity
```
Algorithm:   IP-Hash (or cookie-based affinity)
Why:         WebSocket connections are long-lived and stateful per TCP connection.
             Mid-session rerouting breaks the connection (TCP FIN + reconnect required).
             IP-Hash ensures the same client IP always routes to the same gateway
             instance for the duration of the session.
Failure:     If a gateway instance fails, clients reconnect to any healthy instance.
             They carry their last_seen_message_id and recover missed messages via
             the GET /conversations/{id}/messages?cursor={id} endpoint.
Health:      GET /health every 10s; remove after 3 failures
Drain:       Before removing an instance, wait for connections to idle-disconnect
             (or send WebSocket close frame) to avoid hard drops.
```

### REST API — Round-Robin (No Affinity)
```
Algorithm:   Round-Robin
Why:         REST requests are stateless; any instance can serve any request.
Affinity:    Explicitly NOT used — stateless design goal.
Health:      GET /health every 10s
```

---

## Connection Count Per Instance

| Scenario         | WebSocket connections | Instances needed | Notes                  |
|------------------|-----------------------|------------------|------------------------|
| 10K concurrent   | 1                     | 1                | Single instance fine   |
| 50K concurrent   | 1                     | 1                | Near limit; add second |
| 100K concurrent  | 2                     | 2                | One per 50K            |
| 500K concurrent  | 10                    | 10               | Scale-out              |

Scale-out trigger: any gateway instance exceeds 40,000 active WebSocket connections.
New connections are routed to the new instance by the load balancer.

---

## Redis Pub/Sub Bridges Gateway Instances

This is the key architectural decision that makes multi-instance chat work:

```
User A (connected to Gateway-1) sends a message to Conversation-X.

Gateway-1:
  1. MessageService.SendMessage() → INSERT into PostgreSQL ✓
  2. PUBLISH conversation:X {message} → Redis pub/sub

Redis broadcasts to ALL subscribers of conversation:X:
  - Gateway-1 (has members of X connected) → push to their WebSockets
  - Gateway-2 (has other members of X connected) → push to their WebSockets
  - Gateway-3 (no members of X connected) → receives and discards silently
```

**Result:** User A on Gateway-1 and User B on Gateway-3 both receive the message
within ~5ms. No application-level coordination between gateways.

---

## Stateless Design Guarantees

1. **No gateway-local message buffer.** Every message is in PostgreSQL before
   the pub/sub publish. A gateway restart loses no messages.

2. **Presence state is in Redis.** A user's online status is visible to all
   gateway instances via `EXISTS presence:{userId}`.

3. **Reconnect is instance-agnostic.** A client reconnecting after a gateway
   failure sends its last_seen_message_id to any new gateway instance. The
   instance queries PostgreSQL for missed messages — no cross-instance sync.

4. **Subscription state is per-connection, not per-instance.** When a user
   connects to Gateway-2, Gateway-2 subscribes to that user's conversation
   channels in Redis. When they disconnect, Gateway-2 unsubscribes. No global
   subscription registry.

---

## Scaling Triggers

| Metric                               | Threshold      | Action                                       |
|--------------------------------------|----------------|----------------------------------------------|
| WebSocket connections per instance   | > 40,000       | Add WebSocket gateway instance               |
| Message insert p99                   | > 15ms         | Add PostgreSQL primary capacity (NVMe)       |
| Redis pub/sub delivery latency       | > 20ms         | Add Redis Cluster node                       |
| PostgreSQL replica lag               | > 5s           | Investigate replica; add capacity if needed  |
| Conversation history GET p99         | > 100ms        | Route to faster read replica; check indexes  |
