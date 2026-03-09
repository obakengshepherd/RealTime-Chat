# Failure Scenarios — Real-Time Chat System

> **Status**: Skeleton — stubs defined on Day 2. Full mitigations and implementations added on Day 27.

---

## Scenario 1 — WebSocket Server Restart

**Trigger**: A WebSocket Gateway instance is restarted due to a deployment, OOM kill, or
crash. All clients connected to that instance lose their WebSocket connection.

**Component that fails**: WebSocket Gateway instance.

**Impact**: User-facing — affected users see a disconnection indicator. Messages sent during
the disconnection window are not received in real time.

**Mitigation strategy**: TBD Day 27 — involves client-side reconnection with last-seen
message ID, server-side message replay from PostgreSQL for the reconnect window.

---

## Scenario 2 — Redis Pub/Sub Message Drop

**Trigger**: A Redis pub/sub publish succeeds but the subscriber (WebSocket Gateway) is
momentarily unavailable or busy. Because Redis pub/sub is at-most-once, the message is
not retried.

**Component that fails**: Redis pub/sub delivery guarantee (by design — at-most-once).

**Impact**: User-facing — the recipient does not receive the real-time push. The message
is in PostgreSQL but the client is not aware.

**Mitigation strategy**: TBD Day 27 — involves client-side polling fallback on reconnect
using `GET /conversations/{id}/messages?after={last_id}` to recover missed messages.

---

## Scenario 3 — PostgreSQL Write Failure During Message Send

**Trigger**: MessageService fails to insert the message into PostgreSQL (connection lost,
timeout, constraint violation).

**Component that fails**: PostgreSQL primary.

**Impact**: User-facing — the sender receives an error. The message is not persisted and
is not published to the Redis channel. Recipients do not see it.

**Mitigation strategy**: TBD Day 27 — involves retry with exponential backoff for transient
failures, clear error response for permanent failures, client-side retry UX.

---

## Scenario 4 — Presence Stale After Silent Disconnect

**Trigger**: A mobile client's network connection drops without sending a TCP FIN. The
WebSocket Gateway does not immediately detect the disconnect. The client appears online
until the presence TTL expires.

**Component that fails**: Network layer — silent disconnect not detected by TCP stack.

**Impact**: User-facing — other users see the disconnected user as online for up to 35
seconds after they have actually lost connectivity.

**Mitigation strategy**: TBD Day 27 — involves WebSocket ping/pong keepalive with a
server-side 35-second timeout, presence TTL set to match, explicit cleanup on timeout.

---

## Scenario 5 — Large Group Message Fan-Out Overload

**Trigger**: A message is sent to a group conversation with 500 members. The gateway must
push to 500 WebSocket connections, potentially across multiple instances, within milliseconds.

**Component that fails**: WebSocket Gateway CPU / Redis pub/sub throughput under fan-out.

**Impact**: Internal, potentially user-facing — message delivery latency spikes for all
users on the affected gateway instance, not just those in the large group.

**Mitigation strategy**: TBD Day 27 — involves async push queue per gateway, push
parallelism limits, and fan-out budgets per conversation size.
