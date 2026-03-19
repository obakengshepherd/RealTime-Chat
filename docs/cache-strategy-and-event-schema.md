# Cache Strategy & Event Schema — Real-Time Chat System

---

## Cache Strategy

### Pattern 1: Redis Pub/Sub (Message Delivery Bus)

Chat uses Redis pub/sub as the cross-server delivery mechanism, not as a cache.
It is fire-and-forget — Redis pub/sub is at-most-once delivery by design.

```
SendMessage flow:
  1. INSERT message into PostgreSQL   ← durable storage FIRST
  2. PUBLISH conversation:{id} {msg}  ← real-time delivery SECOND
  3. All subscribed gateways push to connected clients via WebSocket

Reconnect recovery (at-most-once tradeoff handled explicitly):
  1. Client reconnects with last_known_message_id
  2. Server queries: SELECT ... WHERE id > last_known_message_id
  3. Client receives all missed messages from PostgreSQL
```

Why not Kafka for message delivery?

Chat requires sub-100ms delivery. Kafka's consumer model requires polling, which
adds 10-200ms latency per message. Redis pub/sub delivers in <5ms. The tradeoff
is at-most-once vs at-least-once — clients handle recovery explicitly.

### Pattern 2: Presence TTL (Heartbeat Pattern)

```
On WebSocket connect:
  SET presence:{userId} 1 EX 35       ← 35 second TTL

Every 30 seconds (heartbeat):
  EXPIRE presence:{userId} 35          ← renew TTL

On explicit disconnect:
  DEL presence:{userId}                ← immediate removal

Silent disconnect (no TCP FIN):
  Key expires after 35s → user is offline
```

The 35-second TTL accounts for network jitter in the 30-second heartbeat interval.
A user who loses connectivity is marked offline within one TTL window (max 35s).

### Pattern 3: Unread Count Invalidation

Unread counts are expensive to compute (COUNT query per conversation per user).
They are cached with a 60-second TTL and explicitly invalidated when the user
reads messages — ensuring the next read after a `PATCH /messages/{id}/read`
always reflects the current state.

---

## Key Inventory

| Key Pattern                          | Type   | TTL  | Set When              | Invalidated When    |
|--------------------------------------|--------|------|-----------------------|---------------------|
| `conversation:{id}` (pub/sub channel)| PubSub | N/A  | On publish            | At-most-once        |
| `presence:{userId}`                  | String | 35s  | Connect + heartbeat   | Disconnect / TTL    |
| `unread:{userId}:{convId}`           | String | 60s  | Cache-aside reads     | Message read event  |

---

## Event Schema (Chat Uses Redis, Not Kafka)

Chat's real-time delivery is entirely Redis pub/sub. There is no Kafka integration
for the core message path.

### Pub/Sub Channel: `conversation:{conversationId}`

All WebSocket Gateway instances subscribe to channels for their connected users'
conversations. When a message is published to this channel, every subscribed
gateway pushes it to the appropriate WebSocket connections.

**Published event types:**
```json
// New message
{ "type": "new_message", "message_id": "msg_01j9...",
  "conversation_id": "conv_01j9...", "sender_id": "usr_abc123",
  "content": "Hello!", "sent_at": "2024-01-15T10:31:00Z" }

// Message deleted
{ "type": "message_deleted", "message_id": "msg_01j9...",
  "deleted_at": "2024-01-15T10:40:00Z" }
```

### Pub/Sub Channel: `presence:{userId}`

Published when a user's online status changes:
```json
{ "user_id": "usr_abc123", "status": "online" | "offline" }
```
