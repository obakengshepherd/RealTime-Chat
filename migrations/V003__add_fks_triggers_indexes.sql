-- =============================================================================
-- V003__add_cross_table_foreign_keys.sql
-- Real-Time Chat — FK from conversation_members to messages
--
-- ROLLBACK:
--   ALTER TABLE conversation_members
--       DROP CONSTRAINT IF EXISTS conv_members_last_read_msg_fk;
-- =============================================================================

-- Add the FK that could not be defined in V002 (messages table did not exist yet)
ALTER TABLE conversation_members
    ADD CONSTRAINT conv_members_last_read_msg_fk
        FOREIGN KEY (last_read_message_id)
        REFERENCES messages (id)
        ON DELETE SET NULL;
        -- If a message is (soft-)deleted, we do NOT want to lose the member's
        -- read position. But if a message is hard-deleted (theoretically),
        -- SET NULL resets the read cursor gracefully rather than blocking deletion.

-- =============================================================================
-- V004__add_triggers.sql
-- Real-Time Chat — Auto-update triggers
--
-- ROLLBACK:
--   DROP TRIGGER IF EXISTS users_last_seen_trigger ON users;
-- =============================================================================

-- Auto-update users.last_seen_at could be done at the application layer,
-- but this trigger ensures it is always current even for direct DB updates.
-- Note: last_seen_at is explicitly set by the application on disconnect —
-- this trigger is a safety net, not the primary mechanism.

CREATE OR REPLACE FUNCTION update_updated_at_column()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Add updated_at to conversations for last-activity tracking
ALTER TABLE conversations ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW();

CREATE OR REPLACE FUNCTION conversations_updated_at_fn()
RETURNS TRIGGER AS $$
BEGIN
    -- Update the conversation's updated_at whenever a new message is inserted
    UPDATE conversations SET updated_at = NOW() WHERE id = NEW.conversation_id;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER messages_update_conversation_trigger
    AFTER INSERT ON messages
    FOR EACH ROW
    EXECUTE FUNCTION conversations_updated_at_fn();

COMMENT ON TRIGGER messages_update_conversation_trigger ON messages IS
    'Keeps conversations.updated_at current for conversation list ordering '
    '(most recently active conversations first).';

-- =============================================================================
-- V005__add_indexes.sql
-- Real-Time Chat — All performance indexes
--
-- ROLLBACK (reverse order):
--   DROP INDEX IF EXISTS msg_receipts_user_msg_idx;
--   DROP INDEX IF EXISTS msg_receipts_message_id_idx;
--   DROP INDEX IF EXISTS conv_members_user_id_idx;
--   DROP INDEX IF EXISTS messages_conv_id_idx;
--   DROP INDEX IF EXISTS messages_conv_sent_at_idx;
--   DROP INDEX IF EXISTS conversations_updated_at_idx;
-- =============================================================================

-- Query: GET /conversations/{id}/messages — newest messages first
-- Supports cursor-based pagination ordered by sent_at DESC
CREATE INDEX messages_conv_sent_at_idx
    ON messages (conversation_id, sent_at DESC);

COMMENT ON INDEX messages_conv_sent_at_idx IS
    'Paginated message history ordered by newest first.';

-- Query: Cursor-based pagination by message ID (stable under concurrent inserts)
CREATE INDEX messages_conv_id_idx
    ON messages (conversation_id, id DESC)
    WHERE deleted_at IS NULL;
-- Partial index: excludes soft-deleted messages from the active message index

COMMENT ON INDEX messages_conv_id_idx IS
    'Cursor pagination index. Partial (WHERE deleted_at IS NULL) — '
    'excludes deleted messages, keeping the index smaller and faster.';

-- Query: GET /users/{id}/conversations — all conversations for a user
CREATE INDEX conv_members_user_id_idx
    ON conversation_members (user_id);

COMMENT ON INDEX conv_members_user_id_idx IS
    'All conversations a user is a member of — core for inbox loading.';

-- Query: Fetch all receipts for a message (delivery status board)
CREATE INDEX msg_receipts_message_id_idx
    ON message_receipts (message_id);

-- Query: Check what a user has read across multiple messages
CREATE INDEX msg_receipts_user_msg_idx
    ON message_receipts (user_id, message_id);

-- Query: Conversation list ordered by most recently active
CREATE INDEX conversations_updated_at_idx
    ON conversations (updated_at DESC);

ANALYZE users;
ANALYZE conversations;
ANALYZE conversation_members;
ANALYZE messages;
ANALYZE message_receipts;
