# Architecture — Real-Time Chat System

---

## Overview

The Real-Time Chat System is built on a WebSocket-first delivery model with a durable
PostgreSQL message store for persistence and offline recovery. The key architectural
challenge — delivering a message to a recipient who may be connected to a different server
instance — is solved by Redis pub/sub, which acts as the cross-server message bus. Every
server instance subscribes to channels for all conversations whose members are connected
to it; when a message arrives on a channel, every subscribed instance pushes it to the
appropriate WebSocket connections.

---

## Architecture Diagram

```
┌───────────────────────────────────────────────────────────┐
│               Clients (Web / Mobile Apps)                 │
└──────────────────────┬────────────────────────────────────┘
                       │ WebSocket (WSS) + HTTPS
┌──────────────────────▼────────────────────────────────────┐
│                    Load Balancer                           │
│   (IP-Hash Affinity for WS, Round-Robin for REST)        │
└──────────┬───────────────────────────────┬────────────────┘
           │ WebSocket                     │ HTTPS
┌──────────▼──────────┐        ┌───────────▼──────────────┐
│  WebSocket Gateway   │        │       REST API            │
│  (connection mgmt)   │        │  (history, conversations) │
└──────────┬──────────┘        └───────────┬──────────────┘
           │                               │
┌──────────▼──────────────────────────────▼──────────────┐
│                  Message Router                          │
│     (MessageService · ConversationService · Presence)   │
└──────────┬─────────────────────────────────────────────┘
           │
     ┌─────┴──────┐
     │            │
┌────▼───┐  ┌─────▼───────────────────────────────┐
│ Redis   │  │           PostgreSQL                 │
│pub/sub  │  │  (messages · conversations ·        │
│presence │  │   members · receipts)               │
└────┬───┘  └─────────────────────────────────────┘
     │
     └──── All WebSocket Gateway instances subscribe
           to conversation channels and push to
           connected clients
```

---

## Layer-by-Layer Description

### Load Balancer

The load balancer handles two types of traffic with different routing requirements. REST API
requests are stateless and routed round-robin. WebSocket connections are stateful: once a
client upgrades to a WebSocket connection, all subsequent messages on that connection must
go to the same server instance. The load balancer uses IP-hash affinity to route WebSocket
upgrade requests consistently. If a server instance fails, the load balancer redirects the
connection to a healthy instance — the client reconnects and re-establishes presence.

### WebSocket Gateway

The WebSocket Gateway manages the lifecycle of all WebSocket connections. On connection, it
authenticates the client via the Bearer token sent in the upgrade request, registers the user
as online in the Presence layer, and subscribes to Redis pub/sub channels for all conversations
the user is a member of. On disconnect, it deregisters presence and unsubscribes. When a
message arrives on a subscribed channel, the gateway immediately pushes it to the connected
client's WebSocket.

The gateway holds no application state beyond the mapping of user_id to WebSocket connection
handle. All conversation membership, message history, and user data is fetched from the service
layer on demand.

### REST API

The REST API handles operations that are not time-critical: fetching conversation history,
creating conversations, managing conversation members, and retrieving user information. It is
fully stateless and operates independently of the WebSocket gateway. A client that cannot
establish a WebSocket connection can still interact with the system via REST (in degraded
mode: send a message via REST, poll for new messages).

### Message Router / Service Layer

The service layer contains three core services:

**MessageService** handles message creation and delivery. When a message is sent (via WebSocket
or REST), MessageService persists it to PostgreSQL first. After a successful write, it publishes
the message to the Redis pub/sub channel for the conversation (`conversation:{id}`). All
WebSocket Gateway instances subscribed to that channel receive the published message and push
it to any connected members. Persistence-first ensures no message is delivered to the channel
without being durably stored — if the Redis publish fails, the message is still in the database
and will be recovered on the next client sync.

**ConversationService** handles conversation creation, member management, and conversation
list retrieval. It reads and writes to PostgreSQL only — no cache is used for conversation
metadata in this implementation.

**PresenceService** manages online/offline state. On WebSocket connect, it sets
`presence:{user_id}` in Redis with a TTL of 35 seconds. The client sends a WebSocket ping
(heartbeat) every 30 seconds, which renews the TTL. If the TTL expires (client silently
disconnected), the user is automatically considered offline on the next presence check. On
explicit disconnect, the key is deleted immediately. Presence changes are broadcast to
conversation members via Redis pub/sub.

### Cache — Redis

Redis serves two roles. First, it is the pub/sub broker for real-time message delivery —
every message published to a conversation channel is received by every WebSocket Gateway
instance that has subscribers in that conversation. Second, it is the presence store — a
simple key-value TTL store where the existence of the key means the user is online.

Redis pub/sub is at-most-once delivery. If a subscriber (WebSocket Gateway instance) is
unavailable at the moment of publish, the message is not stored or retried. This is an
explicit tradeoff: messages are always persisted to PostgreSQL first, so clients can recover
any missed messages by querying the database after reconnection. Real-time delivery is
best-effort; durable delivery is guaranteed through the persistence layer.

### Database — PostgreSQL

PostgreSQL is the source of truth for all message history and conversation state. The
`messages` table uses soft deletes (`deleted_at`) so that deletion does not break message
receipt records. Pagination on message history uses cursor-based pagination keyed on
`message_id` (a monotonically increasing integer or UUID v7), not offset-based, to remain
stable under concurrent inserts. The `conversation_members` table tracks each user's
`last_read_message_id` for unread count computation.

---

## Component Responsibilities Summary.

| Component           | Responsibility                                        | Communicates Via          |
| ------------------- | ----------------------------------------------------- | ------------------------- |
| Load Balancer       | TLS termination, WS affinity, REST round-robin        | WSS / HTTPS               |
| WebSocket Gateway   | Connection lifecycle, Redis subscribe, push to client | WebSocket + Redis pub/sub |
| REST API            | Message history, conversations, async operations      | HTTP (internal)           |
| MessageService      | Persist message, publish to Redis channel             | PostgreSQL + Redis        |
| ConversationService | Conversation and member management                    | PostgreSQL                |
| PresenceService     | Online/offline state via Redis TTL + heartbeat        | Redis                     |
| Redis               | Pub/sub delivery bus + presence TTL store             | In-memory                 |
| PostgreSQL          | Durable message store, conversation state, receipts   | TCP                       |
