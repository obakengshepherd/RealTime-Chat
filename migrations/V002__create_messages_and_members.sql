-- =============================================================================
-- V002__create_messages_and_members.sql
-- Real-Time Chat System — conversation_members, messages, message_receipts
--
-- ROLLBACK:
--   DROP TABLE IF EXISTS message_receipts CASCADE;
--   DROP TABLE IF EXISTS messages CASCADE;
--   DROP TABLE IF EXISTS conversation_members CASCADE;
-- =============================================================================

CREATE TABLE conversation_members (
    conversation_id       VARCHAR(36)  NOT NULL,
    user_id               VARCHAR(36)  NOT NULL,
    joined_at             TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    last_read_message_id  VARCHAR(36)  NULL,
    -- FK to messages added after messages table is created (V003)

    CONSTRAINT conversation_members_pkey
        PRIMARY KEY (conversation_id, user_id),

    CONSTRAINT conv_members_conversation_fk
        FOREIGN KEY (conversation_id) REFERENCES conversations (id)
        ON DELETE CASCADE,
        -- CASCADE: when a conversation is deleted, remove its member records.
        -- Conversations are rarely deleted but this keeps referential integrity.

    CONSTRAINT conv_members_user_fk
        FOREIGN KEY (user_id) REFERENCES users (id)
        ON DELETE RESTRICT
);

COMMENT ON COLUMN conversation_members.last_read_message_id IS
    'Enables O(1) unread count: COUNT messages WHERE id > last_read_message_id. '
    'FK to messages.id added in V003 after messages table exists.';

-- -----------------------------------------------------------------------------
-- messages
-- The core content entity. Soft-deleted via deleted_at.
-- -----------------------------------------------------------------------------
CREATE TABLE messages (
    id               VARCHAR(36)  NOT NULL,
    conversation_id  VARCHAR(36)  NOT NULL,
    sender_id        VARCHAR(36)  NOT NULL,
    content          TEXT         NULL,
    -- NULL when soft-deleted. Content is cleared on deletion.
    type             msg_type     NOT NULL DEFAULT 'text',
    sent_at          TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    deleted_at       TIMESTAMPTZ  NULL,

    CONSTRAINT messages_pkey PRIMARY KEY (id),

    CONSTRAINT messages_conversation_fk
        FOREIGN KEY (conversation_id) REFERENCES conversations (id)
        ON DELETE RESTRICT,

    CONSTRAINT messages_sender_fk
        FOREIGN KEY (sender_id) REFERENCES users (id)
        ON DELETE RESTRICT,

    -- Content is required for non-deleted text messages
    CONSTRAINT messages_content_when_not_deleted
        CHECK (deleted_at IS NOT NULL OR content IS NOT NULL)

    -- Why soft delete (deleted_at) instead of hard delete?
    -- message_receipts references message IDs. Hard-deleting a message would:
    -- a) Cascade-delete all its receipts (losing the delivery audit trail), OR
    -- b) Leave orphaned receipt records with broken FK references.
    -- Soft delete preserves the message row (for FK integrity) while clearing
    -- content — clients see "Message deleted"; the receipt trail stays intact.
);

COMMENT ON TABLE messages IS
    'Core message entity. Soft-deleted via deleted_at — content set to NULL. '
    'Persisted to PostgreSQL BEFORE publishing to Redis pub/sub channel. '
    'Clients recover missed messages via: SELECT ... WHERE id > last_known_id.';

COMMENT ON COLUMN messages.deleted_at IS
    'NULL = active message. Non-null = soft-deleted. '
    'Content is NULL when deleted_at is set. '
    'Row is retained for referential integrity with message_receipts.';

-- -----------------------------------------------------------------------------
-- message_receipts
-- Delivery and read tracking per message per user.
-- -----------------------------------------------------------------------------
CREATE TABLE message_receipts (
    message_id    VARCHAR(36)  NOT NULL,
    user_id       VARCHAR(36)  NOT NULL,
    delivered_at  TIMESTAMPTZ  NULL,
    read_at       TIMESTAMPTZ  NULL,

    CONSTRAINT message_receipts_pkey PRIMARY KEY (message_id, user_id),

    CONSTRAINT message_receipts_message_fk
        FOREIGN KEY (message_id) REFERENCES messages (id)
        ON DELETE RESTRICT,

    CONSTRAINT message_receipts_user_fk
        FOREIGN KEY (user_id) REFERENCES users (id)
        ON DELETE RESTRICT,

    CONSTRAINT message_receipts_read_after_delivered
        CHECK (read_at IS NULL OR delivered_at IS NULL OR read_at >= delivered_at)
);

COMMENT ON TABLE message_receipts IS
    'Per-user delivery and read status for each message. '
    'delivered_at: when pushed to the client WebSocket. '
    'read_at: when the user''s UI registered the message as viewed.';
