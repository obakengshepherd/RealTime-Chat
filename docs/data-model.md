# Data Model — Real-Time Chat System

---

## Database Technology Choices

### PostgreSQL (Durable message store)
Every message is persisted to PostgreSQL before being published to the Redis pub/sub
channel. PostgreSQL is the authoritative message store — real-time delivery is best-effort;
durable delivery is guaranteed through the database. Cursor-based pagination on message
history uses the `messages.id` column as the stable cursor, which does not drift under
concurrent inserts the way offset-based pagination does.

### Redis (Pub/sub delivery and presence)
Redis serves two roles: the real-time message delivery bus (pub/sub, channel per
conversation), and the presence store (TTL-keyed by user ID). Neither role requires
durability — pub/sub is at-most-once by design, and presence state self-heals within
one heartbeat window. Redis persistence (`AOF`) is enabled to reduce the cold-start
penalty after a restart, but Redis is never the source of truth.

---

## Entity Relationship Overview

A **User** can be a member of multiple **Conversations**. The join table
**ConversationMembers** represents this many-to-many relationship and carries per-member
state: when they joined and which message they last read.

A **Conversation** contains an ordered sequence of **Messages**. Messages belong to
exactly one conversation and are sent by exactly one user. They are soft-deleted (the
content is cleared and `deleted_at` is set) rather than hard-deleted, because
**MessageReceipts** reference message IDs — hard deletion would orphan receipt records.

---

## Table Definitions

### `users`

| Column        | Type          | Constraints                        | Description                          |
|---------------|---------------|------------------------------------|--------------------------------------|
| `id`          | `VARCHAR(36)` | PRIMARY KEY                        | Prefixed UUID: `usr_<uuid>`          |
| `username`    | `VARCHAR(64)` | NOT NULL, UNIQUE                   | Display name                         |
| `email`       | `VARCHAR(255)`| NOT NULL, UNIQUE                   | Login identifier                     |
| `status`      | `user_status` | NOT NULL, DEFAULT 'active'         | Enum: `active`, `suspended`          |
| `last_seen_at`| `TIMESTAMPTZ` | NULL                               | Updated on disconnect                |
| `created_at`  | `TIMESTAMPTZ` | NOT NULL, DEFAULT NOW()            | Registration timestamp               |

### `conversations`

| Column       | Type             | Constraints               | Description                            |
|--------------|------------------|---------------------------|----------------------------------------|
| `id`         | `VARCHAR(36)`    | PRIMARY KEY               | Prefixed UUID: `conv_<uuid>`           |
| `type`       | `conv_type`      | NOT NULL                  | Enum: `direct`, `group`               |
| `name`       | `VARCHAR(128)`   | NULL                      | Group name — null for direct chats    |
| `created_by` | `VARCHAR(36)`    | NOT NULL, FK → users      | Creator                               |
| `created_at` | `TIMESTAMPTZ`    | NOT NULL, DEFAULT NOW()   | Immutable                             |

### `conversation_members`

| Column                 | Type          | Constraints                         | Description                                  |
|------------------------|---------------|-------------------------------------|----------------------------------------------|
| `conversation_id`      | `VARCHAR(36)` | NOT NULL, FK → conversations        | Composite PK part 1                          |
| `user_id`              | `VARCHAR(36)` | NOT NULL, FK → users                | Composite PK part 2                          |
| `joined_at`            | `TIMESTAMPTZ` | NOT NULL, DEFAULT NOW()             | When the user joined                         |
| `last_read_message_id` | `VARCHAR(36)` | NULL, FK → messages                 | Last message this user has read              |

PRIMARY KEY: `(conversation_id, user_id)`

**`last_read_message_id`** enables efficient unread count computation without scanning
all messages: `SELECT COUNT(*) FROM messages WHERE conversation_id = ? AND id > last_read_message_id`.

### `messages`

| Column            | Type          | Constraints                     | Description                                    |
|-------------------|---------------|---------------------------------|------------------------------------------------|
| `id`              | `VARCHAR(36)` | PRIMARY KEY                     | Prefixed UUID: `msg_<uuid>`                    |
| `conversation_id` | `VARCHAR(36)` | NOT NULL, FK → conversations    | Parent conversation                            |
| `sender_id`       | `VARCHAR(36)` | NOT NULL, FK → users            | Message author                                 |
| `content`         | `TEXT`        | NULL                            | NULL when soft-deleted                         |
| `type`            | `msg_type`    | NOT NULL, DEFAULT 'text'        | Enum: `text`, `system`                         |
| `sent_at`         | `TIMESTAMPTZ` | NOT NULL, DEFAULT NOW()         | When the server received the message           |
| `deleted_at`      | `TIMESTAMPTZ` | NULL                            | Set on soft delete; NULL = not deleted         |

**Why soft delete (`deleted_at`) instead of hard delete?** Message receipts reference
message IDs. If a message were hard-deleted, its receipts would either need cascaded
deletion (losing delivery audit trail) or become orphaned foreign keys. With soft delete,
the message row remains with `content = NULL` and `deleted_at` set — the row exists for
referential integrity, but clients display it as "Message deleted."

### `message_receipts`

| Column         | Type          | Constraints              | Description                         |
|----------------|---------------|--------------------------|-------------------------------------|
| `message_id`   | `VARCHAR(36)` | NOT NULL, FK → messages  | Composite PK part 1                 |
| `user_id`      | `VARCHAR(36)` | NOT NULL, FK → users     | Composite PK part 2                 |
| `delivered_at` | `TIMESTAMPTZ` | NULL                     | When pushed to the client's device  |
| `read_at`      | `TIMESTAMPTZ` | NULL                     | When the user viewed the message    |

PRIMARY KEY: `(message_id, user_id)`

---

## Index Strategy

| Index Name                              | Table                   | Columns                          | Type    | Query Pattern                                    |
|-----------------------------------------|-------------------------|----------------------------------|---------|--------------------------------------------------|
| `users_email_uniq`                      | `users`                 | `(email)`                        | UNIQUE  | Login lookup                                     |
| `messages_conv_sent_at_idx`             | `messages`              | `(conversation_id, sent_at DESC)`| B-tree  | Fetch latest messages in a conversation          |
| `messages_conv_id_idx`                  | `messages`              | `(conversation_id, id DESC)`     | B-tree  | Cursor-based pagination by message ID            |
| `conv_members_user_id_idx`              | `conversation_members`  | `(user_id)`                      | B-tree  | Fetch all conversations for a user               |
| `msg_receipts_message_id_idx`           | `message_receipts`      | `(message_id)`                   | B-tree  | Fetch all receipts for a message                 |
| `msg_receipts_user_conv_idx`            | `message_receipts`      | `(user_id, message_id)`          | B-tree  | Check read status for a user across messages     |

---

## Relationship Types

- **User → Conversations**: many-to-many, via `conversation_members`.
- **Conversation → Messages**: one-to-many, ordered by `sent_at`.
- **Message → MessageReceipts**: one-to-many (one receipt per member who received it).
- **ConversationMember → Message** (`last_read_message_id`): many-to-one self-reference within conversation.

---

## Soft Delete Strategy

Messages use `deleted_at` soft delete as described above.

Users use a `status` enum rather than deletion — preserving the sender_id reference on
their historical messages.

Conversations are never deleted — archiving could be a future feature via a status flag.

---

## Audit Trail

| Table                  | `created_at` | `updated_at` | Notes                                          |
|------------------------|--------------|--------------|------------------------------------------------|
| `users`                | ✓            | `last_seen_at` | `last_seen_at` updated on disconnect         |
| `conversations`        | ✓            | ✗            | Immutable after creation                       |
| `conversation_members` | `joined_at`  | ✗            | `last_read_message_id` updated on read receipt |
| `messages`             | `sent_at`    | `deleted_at` | Only modification is soft delete               |
| `message_receipts`     | ✗            | ✗            | `delivered_at` and `read_at` are set once      |
