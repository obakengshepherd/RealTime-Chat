# Problem Statement — Real-Time Chat System

---

## Section 1 — The Problem.

Real-time messaging is one of the most interaction-dense features in modern software. Users
in a chat application expect messages to appear on the recipient's screen within milliseconds
of being sent, to see when others are online, and to never lose a message they sent — even
if their connection drops and recovers. At the scale of tens of thousands of concurrent users,
delivering this experience requires a fundamentally different architecture than a standard
request-response API. A naive polling approach collapses under the volume; a poorly designed
WebSocket server cannot scale across machines; and a system without persistent storage fails
the moment a user goes offline and comes back.

---

## Section 2 — Why It Is Hard

- **Connection state at scale**: WebSocket connections are stateful and long-lived. A single
  server can maintain tens of thousands of connections, but once you need more than one server,
  a message published from Server A must reach a user connected to Server B. This requires a
  pub/sub layer that bridges server instances.

- **Message ordering**: In a distributed system, messages from different senders can arrive
  at the storage layer out of order. Recipients must see messages in the order they were sent
  within a conversation, not in the order they arrived at the server.

- **Offline delivery**: A recipient who is temporarily disconnected must receive all messages
  sent during their absence when they reconnect. The system must distinguish between "delivered
  to device" and "stored durably for future delivery."

- **Presence accuracy**: Showing a user as "online" when they have silently disconnected (e.g.
  mobile app backgrounded, network switch) misleads senders. The system must detect and
  propagate presence changes reliably, including silent disconnections that generate no explicit
  logout event.

- **Fan-out at scale**: A message in a group conversation with 500 members must be delivered
  to up to 500 concurrent WebSocket connections, potentially across many server instances,
  within milliseconds.

---

## Section 3 — Scope of This Implementation.

**In scope:**

- Direct (one-to-one) and group conversations
- Real-time message delivery via WebSocket connections
- Redis pub/sub as the cross-server message delivery bridge
- Persistent message storage in PostgreSQL for history and offline recovery
- Cursor-based message history pagination
- Delivery receipts (message delivered to server) and read receipts
- User presence tracking with Redis TTL-based heartbeat
- Reconnect recovery: clients re-fetch missed messages using last-seen message ID

**Out of scope:**

- End-to-end encryption
- File, image, or media message attachments
- Voice or video calling
- Message search
- Push notifications to mobile devices (assumed handled externally)
- Message translation or content moderation

---

## Section 4 — Success Criteria.

The system is working correctly when:

1. A message sent by User A is received by an online User B within 100ms under normal load,
   regardless of whether they are connected to the same server instance.

2. A user who was offline for any duration receives all messages sent during their absence,
   in the correct order, upon reconnection — with no duplicates.

3. Presence status accurately reflects reality within 30 seconds: a user who disconnects
   without an explicit logout is marked offline within one heartbeat TTL window.

4. Message ordering within a conversation is consistent — all users see the same sequence
   of messages, and no message appears before one it was demonstrably sent after.

5. A single server restart does not result in message loss — clients reconnect and recover
   state from persistent storage.

6. The system sustains 100,000 concurrent WebSocket connections and 10,000 messages per
   second without degradation in delivery latency.
