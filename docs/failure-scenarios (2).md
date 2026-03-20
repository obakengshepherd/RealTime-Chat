# Failure Scenarios — Real-Time Chat System

> **Status**: Complete — Days 25–27 implementation. Replaces Phase 1 skeleton.

---

## Scenario 1 — WebSocket Server Restart (Active Connections Dropped)

**Trigger**
A WebSocket Gateway instance is restarted: planned deployment, OOM kill, or crash.
All WebSocket connections to that instance are terminated with a TCP FIN or RST.

**Affected Components**
WebSocket Gateway (ASP.NET), all active connections on the restarted instance,
Redis pub/sub subscriptions for those connections.

**User-Visible Impact**
Affected users see a brief disconnection indicator in the client UI (typically
1–3 seconds). Messages sent by others during the reconnect window are not received
in real-time. No messages are permanently lost.

**System Behaviour Without Mitigation**
Client WebSocket connection terminates. If the client does not reconnect
automatically, the user must manually refresh the page. Any messages sent to
the conversation during the disconnection window are missed permanently.

**Mitigation**

1. **Automatic client reconnection with last-seen cursor:** On disconnect,
   the client immediately reconnects with its `last_message_id` stored in
   local state. The server never needs to track what a client last received.

2. **Message replay on reconnect:** Upon reconnection, the client calls:
   ```
   GET /api/v1/conversations/{id}/messages?cursor={last_message_id}&limit=50
   ```
   for each conversation it is a member of. The server returns all messages
   newer than the cursor from PostgreSQL. The client inserts them into the
   local message list, filling any gap silently.

3. **Redis subscription recovery:** When the client reconnects to a new
   (or restarted) gateway instance, the gateway subscribes to the client's
   conversation channels in Redis. This re-establishes the pub/sub pipeline
   automatically — no application-level coordination required.

4. **Deployment drain strategy:** Before a planned restart, the load balancer
   sends existing connections a WebSocket close frame with code 1001 (Going Away).
   Well-behaved clients reconnect gracefully. The gateway waits up to 30 seconds
   for connections to close naturally before forcing termination.

**Detection**
- Metric: `websocket_reconnections_total` spike during deployment window.
- Alert: `websocket_disconnection_rate > 10%` outside of deployment windows →
  unexpected gateway issue.
- Log: Reconnect with cursor query logged at DEBUG level; gap size logged at INFO.

---

## Scenario 2 — Redis Pub/Sub Message Drop (At-Most-Once Delivery)

**Trigger**
A message is published via Redis pub/sub to the `conversation:{id}` channel.
The subscribing WebSocket Gateway is momentarily paused (GC pause, CPU spike,
network buffer full). Redis does not retry pub/sub delivers — the message is
silently dropped.

**Affected Components**
Redis pub/sub delivery, WebSocket Gateway subscriber.

**User-Visible Impact**
The recipient does not receive the real-time push. Their chat UI does not update.
The message IS in PostgreSQL. The recipient only sees it on their next session
load or manual refresh.

**System Behaviour Without Mitigation**
The message is lost for the recipient's current session. They may remain unaware
until they refresh or receive a subsequent message that triggers a resync.

**Mitigation — Explicit Tradeoff**
This is an **intentional design decision**: Redis pub/sub is at-most-once by design.
The tradeoff is: ultra-low delivery latency (<5ms) in exchange for occasional missed
real-time pushes.

The recovery path is explicitly designed:

1. **Client polling on reconnect:** The client polls
   `GET /messages?cursor={last_id}` immediately on WebSocket reconnect and
   periodically (every 60 seconds) as a background sync heartbeat. Missed
   messages appear within one poll cycle.

2. **Unread count badge:** The server maintains an `unread_count` in
   `conversation_members.last_read_message_id`. A message badge update
   reaches the client even if the pub/sub push was dropped, because the
   badge update is driven by the cursor comparison on next load.

3. **Alternative considered:** Using Kafka for message delivery would give
   at-least-once guarantees but adds 10–200ms latency per message (consumer
   poll interval). For real-time chat, sub-5ms pub/sub delivery is the right
   tradeoff. Kafka is appropriate for durable audit logs, not interactive UX.

**Detection**
- Metric: `pubsub_message_drop_rate` — measurable only via client-reported gaps
  (e.g., `cursor_gap_size` on reconnect).
- Alert: `cursor_gap_size > 10` on reconnect more than 5% of sessions →
  clients are missing messages consistently; investigate gateway health.

---

## Scenario 3 — Message Storm (Single Conversation Flood)

**Trigger**
A bot or misbehaving client sends 10,000 messages/second to a single conversation.
Other members receive a WebSocket push for every message, overwhelming their
connections and the gateway's fan-out capacity.

**Affected Components**
MessageService, Redis pub/sub fan-out, all WebSocket connections of conversation members.

**User-Visible Impact**
Members of the affected conversation experience UI freezing or extreme lag.
Other conversations on the same gateway instance may be affected if the gateway
is CPU-saturated from the fan-out.

**System Behaviour Without Mitigation**
Each `SendMessageAsync` call inserts to PostgreSQL, publishes to Redis, and the
gateway pushes to all members. At 10K msg/sec with 50 members, that is 500K WebSocket
sends/sec — far beyond a single gateway's capacity.

**Mitigation**

1. **Per-conversation rate limiting:** The `ChatPolicies` rate limiter in
   `RateLimitPolicies` limits `POST /conversations/{id}/messages` to 60/minute
   per user. Rate key includes the conversation ID so heavy senders in large
   groups are isolated from other conversations.

2. **Message coalescing for high-frequency conversations:** If a conversation
   receives more than 100 messages in 10 seconds, the gateway switches to
   coalescing mode: instead of pushing each message individually, it pushes
   a `"new_messages_available"` signal with a count. Members' clients respond
   by pulling the batch via `GET /messages?cursor={last_id}`.

3. **Conversation-level circuit breaker:** If fan-out failures exceed 20% for
   a conversation in 30 seconds, the circuit opens: that conversation's messages
   are queued rather than pushed. Members pull on their next poll cycle.

4. **Sender throttling at the PostgreSQL level:** The `messages` table has an
   advisory lock per conversation during `INSERT`, preventing parallel inserts
   from the same client from racing.

**Detection**
- Metric: `conversation_message_rate` gauge per conversation. Alert > 100/min.
- Alert: `rate_limit_exceeded_total` for `message-send` policy > 50/min for
  a single user → likely bot or abuse.
- Alert: Gateway CPU > 80% → possible fan-out storm.

---

## Scenario 4 — Stale Presence After Silent Network Disconnect

**Trigger**
A mobile client's network drops without a clean TCP teardown (e.g., airplane mode,
tunnel). No WebSocket close frame is sent. The gateway does not immediately detect
the dead connection.

**Affected Components**
PresenceService, Redis presence keys, WebSocket Gateway keepalive.

**User-Visible Impact**
Other users see the disconnected user as "online" for up to 35 seconds after the
physical disconnect. Sent messages appear as "delivered" when they were not.

**System Behaviour Without Mitigation**
Redis `presence:{userId}` key remains set. IsOnline returns true. The client's
app shows online indicators. Messages pile up undelivered in the gateway's
write buffer until the TCP stack eventually times out (up to several minutes
for mobile networks).

**Mitigation**

1. **WebSocket ping/pong keepalive:** The gateway sends a WebSocket PING frame
   every 25 seconds. If no PONG is received within 10 seconds, the connection
   is declared dead and explicitly closed.

2. **Presence TTL matches ping cycle:** `presence:{userId}` TTL is 35 seconds.
   Client sends a heartbeat request every 30 seconds to renew it. If the client
   is unreachable (ping/pong fails), no heartbeat fires, and the TTL expires
   within 35 seconds → user appears offline automatically.

3. **Explicit cleanup on detected disconnect:** When the gateway detects a dead
   connection via ping/pong failure, it immediately calls `PresenceService.SetOfflineAsync`
   (deletes the Redis key) rather than waiting for TTL expiry.

**Detection**
- Metric: `presence_ttl_expiry_total` vs `presence_explicit_logout_total` —
  high ratio of TTL expiries to explicit logouts indicates many silent disconnects
  (expected on mobile; alert only if > 50% over sustained period).

---

## Scenario 5 — Large Group Message Fan-Out Overload

**Trigger**
A message is sent to a group conversation with 500 members spread across 3 gateway
instances. Each gateway must push to its subset of 500 connections.

**Affected Components**
Redis pub/sub (fan-out), WebSocket Gateway CPU, all 500 member connections.

**User-Visible Impact**
In extreme cases: noticeable delivery latency for the 500-member group, and
potentially increased latency for unrelated conversations on the same gateway
instance if the gateway CPU is saturated.

**Mitigation**

1. **Async push with bounded parallelism:** Each gateway pushes to its local
   connections asynchronously using `Parallel.ForEachAsync` with a `MaxDegreeOfParallelism`
   of 50. This prevents a 500-member push from blocking the gateway's main loop.

2. **Separate gateway pool for large groups (>200 members):** Conversations
   with > 200 members are directed to a dedicated "large group" gateway tier with
   more CPU allocation. This isolates large group fan-out from the general
   message delivery path.

3. **Fan-out budget enforcement:** If a single gateway connection push takes
   longer than 100ms, it is abandoned and the member is expected to pull on their
   next poll cycle. This bounds the worst-case gateway stall.

**Detection**
- Metric: `pubsub_fanout_duration_ms` histogram. Alert if p95 > 50ms.
- Metric: `gateway_connections_count` per instance. Alert > 40,000.

---

## Universal Scenarios

### U1 — Kafka Consumer Lag
Not applicable to Chat — Chat uses Redis pub/sub, not Kafka.

### U2 — Database Connection Pool Exhaustion
**Specific impact for Chat:** Message inserts stall; the sender receives a 503 and
must retry. No message loss (retry is safe with idempotency key on `POST /messages`).
**Detection:** `pgbouncer_wait_time_p99 > 50ms` for the chat service.

### U3 — Downstream Service Timeout
Not applicable — Chat has no synchronous downstream service calls.
