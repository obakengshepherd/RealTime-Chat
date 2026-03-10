# API Specification — Real-Time Chat System

---

## Overview

The Real-Time Chat API handles conversation management, message persistence, and message
history retrieval. Real-time delivery itself occurs over WebSocket connections (documented
separately in `docs/websocket-design.md`); this REST API is the complement — it handles
history, conversation management, and operations that do not require sub-second latency.
Consumed by web and mobile clients and by the notification service.

---

## Base URL and Versioning

```
https://api.chat.internal/api/v1
```

WebSocket endpoint: `wss://ws.chat.internal/v1/connect`

---

## Authentication

```
Authorization: Bearer <jwt_token>
```

For WebSocket connections, the Bearer token is passed as a query parameter on the upgrade
request: `wss://ws.chat.internal/v1/connect?token=<jwt_token>`. The middleware validates
the token on connection upgrade; invalid tokens reject the upgrade with HTTP 401.

---

## Common Response Envelope

### Success
```json
{
  "data": { ... },
  "meta": { "request_id": "uuid", "timestamp": "2024-01-15T10:30:00Z" }
}
```

### Error
```json
{
  "error": {
    "code": "NOT_CONVERSATION_MEMBER",
    "message": "You are not a member of this conversation.",
    "details": []
  },
  "meta": { "request_id": "uuid", "timestamp": "2024-01-15T10:30:00Z" }
}
```

---

## Rate Limiting

| Endpoint                              | Limit              | Scope     |
|--------------------------------------|--------------------|-----------|
| `POST /conversations/{id}/messages`  | 60 / minute        | Per user  |
| `POST /conversations`                | 10 / minute        | Per user  |
| All other endpoints                  | 120 / minute       | Per user  |

---

## Endpoints

---

### POST /conversations

**Description:** Creates a new conversation. For `direct` type, exactly two member IDs are
required (including the caller). For `group`, 2 to 500 members.

**Request Body:**

| Field      | Type           | Required | Validation              | Example            |
|------------|----------------|----------|-------------------------|--------------------|
| `type`     | string         | Yes      | `direct` or `group`     | `"group"`          |
| `name`     | string         | No       | max 128 chars (group)   | `"Project Alpha"`  |
| `member_ids` | string[]     | Yes      | 2–500 user IDs          | `["usr_1","usr_2"]`|

**Response — 201 Created:**
```json
{
  "data": {
    "id": "conv_01j9z3k4m5n6p7q8",
    "type": "group",
    "name": "Project Alpha",
    "member_count": 3,
    "created_by": "usr_abc123",
    "created_at": "2024-01-15T10:30:00Z"
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                                              |
|------|--------------------------------------------------------|
| 201  | Conversation created                                   |
| 400  | Invalid type, missing members, too many/few members    |
| 401  | Unauthorized                                           |
| 409  | Direct conversation between these two users exists     |

---

### POST /conversations/{id}/messages

**Description:** Sends a message to a conversation. The caller must be a member.
Also triggers Redis pub/sub for real-time delivery to connected clients.

**Path Parameters:** `id` — Conversation ID

**Request Headers:** `X-Idempotency-Key: <uuid-v4>` *(recommended)*

**Request Body:**

| Field     | Type   | Required | Validation               | Example          |
|-----------|--------|----------|--------------------------|------------------|
| `content` | string | Yes      | 1–4000 chars, non-empty  | `"Hello there!"` |
| `type`    | string | No       | `text` (default)         | `"text"`         |

**Response — 201 Created:**
```json
{
  "data": {
    "id": "msg_01j9z3k4m5n6p7q8",
    "conversation_id": "conv_01j9z3k4m5n6p7q8",
    "sender_id": "usr_abc123",
    "content": "Hello there!",
    "type": "text",
    "sent_at": "2024-01-15T10:31:00Z"
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                            |
|------|--------------------------------------|
| 201  | Message sent and persisted           |
| 400  | Empty content or content too long    |
| 401  | Unauthorized                         |
| 403  | Caller is not a conversation member  |
| 404  | Conversation not found               |
| 429  | Rate limit exceeded                  |

---

### GET /conversations/{id}/messages

**Description:** Returns paginated message history for a conversation. Caller must be a
member. Ordered by `sent_at` descending. Cursor-based pagination.

**Path Parameters:** `id` — Conversation ID

**Query Parameters:**

| Parameter | Type    | Default | Description                     |
|-----------|---------|---------|---------------------------------|
| `limit`   | integer | `50`    | Page size, max 100              |
| `cursor`  | string  | —       | Opaque cursor from previous page|
| `before`  | string  | —       | ISO8601 datetime upper bound    |

**Response — 200 OK:**
```json
{
  "data": [
    {
      "id": "msg_01j9z3k4m5n6p7q8",
      "sender_id": "usr_abc123",
      "content": "Hello there!",
      "type": "text",
      "sent_at": "2024-01-15T10:31:00Z",
      "deleted_at": null
    }
  ],
  "pagination": { "cursor": "eyJpZCI6Im1zZyJ9", "has_more": true, "limit": 50 },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                            |
|------|--------------------------------------|
| 200  | Success                              |
| 401  | Unauthorized                         |
| 403  | Caller is not a conversation member  |
| 404  | Conversation not found               |

---

### GET /users/{id}/conversations

**Description:** Returns all conversations the specified user is a member of, ordered by
most recent message descending.

**Path Parameters:** `id` — User ID (must match authenticated user)

**Query Parameters:**

| Parameter | Type    | Default | Description        |
|-----------|---------|---------|--------------------|
| `limit`   | integer | `20`    | Page size, max 50  |
| `cursor`  | string  | —       | Pagination cursor  |

**Response — 200 OK:**
```json
{
  "data": [
    {
      "id": "conv_01j9z3k4m5n6p7q8",
      "type": "group",
      "name": "Project Alpha",
      "last_message": {
        "content": "See you tomorrow",
        "sender_id": "usr_def456",
        "sent_at": "2024-01-15T10:31:00Z"
      },
      "unread_count": 3
    }
  ],
  "pagination": { ... },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                              |
|------|----------------------------------------|
| 200  | Success                                |
| 401  | Unauthorized                           |
| 403  | User ID does not match token           |

---

### PATCH /messages/{id}/read

**Description:** Marks a specific message (and all messages before it in the same
conversation) as read by the authenticated user. Updates `last_read_message_id`.

**Path Parameters:** `id` — Message ID

**Response — 200 OK:**
```json
{
  "data": {
    "message_id": "msg_01j9z3k4m5n6p7q8",
    "read_at": "2024-01-15T10:35:00Z"
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                            |
|------|--------------------------------------|
| 200  | Read receipt recorded                |
| 401  | Unauthorized                         |
| 403  | User is not a member of the conversation |
| 404  | Message not found                    |

---

### DELETE /messages/{id}

**Description:** Soft-deletes a message sent by the authenticated user. Sets `deleted_at`
timestamp and replaces content with a deletion marker for other members. Message record
and receipts are retained.

**Path Parameters:** `id` — Message ID

**Response — 200 OK:**
```json
{
  "data": {
    "id": "msg_01j9z3k4m5n6p7q8",
    "deleted_at": "2024-01-15T10:40:00Z"
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                                     |
|------|-----------------------------------------------|
| 200  | Message soft-deleted                          |
| 401  | Unauthorized                                  |
| 403  | Caller did not send this message              |
| 404  | Message not found                             |
| 422  | Message already deleted                       |
