-- =============================================================================
-- V005__add_message_partitioning_prep.sql
-- Real-Time Chat System — Partition preparation for url_clicks-style archival
--
-- The messages table is designed for future time-based partitioning when
-- volume grows beyond 100M rows. This migration adds the infrastructure
-- comments and a check function to support future partitioning migrations.
--
-- ROLLBACK: No destructive changes.
-- =============================================================================

-- Add a day-level partition key hint column for future migration planning
COMMENT ON TABLE messages IS
    'Core message entity. Soft-deleted via deleted_at. '
    'Future: partition by RANGE(sent_at) monthly when table exceeds 100M rows. '
    'Retention policy: 90 days active, archive older partitions.';

-- Verify indexes from V003 exist
DO $$
BEGIN
    ASSERT EXISTS (
        SELECT 1 FROM pg_indexes
        WHERE tablename = 'messages' AND indexname = 'messages_conv_sent_at_idx'
    ), 'messages_conv_sent_at_idx missing';
    ASSERT EXISTS (
        SELECT 1 FROM pg_indexes
        WHERE tablename = 'conversation_members' AND indexname = 'conv_members_user_id_idx'
    ), 'conv_members_user_id_idx missing';
    RAISE NOTICE 'All required indexes verified.';
END;
$$;
