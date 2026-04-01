-- =============================================================================
-- V006__seed_test_data.sql
-- Real-Time Chat System — Test users and conversations
--
-- This migration creates 5 test users and 3 conversations for manual testing.
-- Safe to run multiple times (uses ON CONFLICT DO NOTHING).
--
-- Test Users:
--   alice_123  : Alice Johnson
--   bob_456    : Bob Smith
--   charlie_789: Charlie Davis
--   diana_101  : Diana White
--   eve_202    : Eve Brown
--
-- ROLLBACK:
--   DELETE FROM message_receipts WHERE message_id IN (
--     SELECT id FROM messages WHERE conversation_id IN (
--       SELECT id FROM conversations WHERE creator_id IN (
--         SELECT id FROM users WHERE id LIKE 'test_%'
--       )
--     )
--   );
--   DELETE FROM messages WHERE conversation_id IN (
--     SELECT id FROM conversations WHERE created_by IN (
--       SELECT id FROM users WHERE id LIKE 'test_%'
--     )
--   );
--   DELETE FROM conversation_members WHERE conversation_id IN (
--     SELECT id FROM conversations WHERE created_by IN (
--       SELECT id FROM users WHERE id LIKE 'test_%'
--     )
--   );
--   DELETE FROM conversations WHERE created_by IN (
--     SELECT id FROM users WHERE id LIKE 'test_%'
--   );
--   DELETE FROM users WHERE id LIKE 'test_%';
-- =============================================================================

-- Create test users
INSERT INTO users (id, username, email, status, created_at)
VALUES
    ('test_alice_123',   'alice',    'alice@example.com',    'active', NOW()),
    ('test_bob_456',     'bob',      'bob@example.com',      'active', NOW()),
    ('test_charlie_789', 'charlie',  'charlie@example.com',  'active', NOW()),
    ('test_diana_101',   'diana',    'diana@example.com',    'active', NOW()),
    ('test_eve_202',     'eve',      'eve@example.com',      'active', NOW())
ON CONFLICT (id) DO NOTHING;

-- Create test conversations
-- 1. Direct conversation: alice ↔ bob
INSERT INTO conversations (id, type, created_by, created_at)
VALUES
    ('test_conv_dm_alice_bob', 'direct', 'test_alice_123', NOW())
ON CONFLICT (id) DO NOTHING;

-- 2. Group conversation: alice, charlie, diana (team-design)
INSERT INTO conversations (id, type, name, created_by, created_at)
VALUES
    ('test_conv_group_1', 'group', 'Team Design', 'test_alice_123', NOW())
ON CONFLICT (id) DO NOTHING;

-- 3. Group conversation: bob, eve, charlie (team-engineering)
INSERT INTO conversations (id, type, name, created_by, created_at)
VALUES
    ('test_conv_group_2', 'group', 'Team Engineering', 'test_bob_456', NOW())
ON CONFLICT (id) DO NOTHING;

-- Add members to conversations
INSERT INTO conversation_members (conversation_id, user_id, joined_at)
VALUES
    -- Direct: alice ↔ bob
    ('test_conv_dm_alice_bob', 'test_alice_123', NOW()),
    ('test_conv_dm_alice_bob', 'test_bob_456',   NOW()),

    -- Group 1: alice, charlie, diana
    ('test_conv_group_1', 'test_alice_123',   NOW()),
    ('test_conv_group_1', 'test_charlie_789', NOW()),
    ('test_conv_group_1', 'test_diana_101',   NOW()),

    -- Group 2: bob, eve, charlie
    ('test_conv_group_2', 'test_bob_456',     NOW()),
    ('test_conv_group_2', 'test_eve_202',     NOW()),
    ('test_conv_group_2', 'test_charlie_789', NOW())
ON CONFLICT (conversation_id, user_id) DO NOTHING;

-- Create test messages in each conversation
-- Direct conversation: alice → bob (3 messages)
INSERT INTO messages (id, conversation_id, sender_id, content, type, sent_at)
VALUES
    ('test_msg_1', 'test_conv_dm_alice_bob', 'test_alice_123', 'Hey Bob! How are you?', 'text', NOW()),
    ('test_msg_2', 'test_conv_dm_alice_bob', 'test_bob_456',   'Hi Alice! All good here. You?', 'text', NOW()),
    ('test_msg_3', 'test_conv_dm_alice_bob', 'test_alice_123', 'Great! Catch up later? 👋', 'text', NOW() - INTERVAL '1 minute')
ON CONFLICT (id) DO NOTHING;

-- Group 1: alice → charlie → diana (5 messages)
INSERT INTO messages (id, conversation_id, sender_id, content, type, sent_at)
VALUES
    ('test_msg_g1_1', 'test_conv_group_1', 'test_alice_123',   'Team! Let''s sync on the design mockups.', 'text', NOW() - INTERVAL '2 minutes'),
    ('test_msg_g1_2', 'test_conv_group_1', 'test_charlie_789', 'Already started. Designs look amazing! 🎨', 'text', NOW() - INTERVAL '90 seconds'),
    ('test_msg_g1_3', 'test_conv_group_1', 'test_diana_101',   'Agreed. Love the color palette.', 'text', NOW() - INTERVAL '60 seconds'),
    ('test_msg_g1_4', 'test_conv_group_1', 'test_alice_123',   'Great feedback! I''ll update the specs.', 'text', NOW() - INTERVAL '30 seconds'),
    ('test_msg_g1_5', 'test_conv_group_1', 'test_charlie_789', 'Perfect. When can we review v2?', 'text', NOW() - INTERVAL '10 seconds')
ON CONFLICT (id) DO NOTHING;

-- Group 2: bob → eve → charlie (4 messages)
INSERT INTO messages (id, conversation_id, sender_id, content, type, sent_at)
VALUES
    ('test_msg_g2_1', 'test_conv_group_2', 'test_bob_456',     'Released the new API v2 to staging!', 'text', NOW() - INTERVAL '3 minutes'),
    ('test_msg_g2_2', 'test_conv_group_2', 'test_eve_202',     'Excellent! I''ll run load tests. 🚀', 'text', NOW() - INTERVAL '2 minutes'),
    ('test_msg_g2_3', 'test_conv_group_2', 'test_charlie_789', 'Performance looks good. Ready for prod?', 'text', NOW() - INTERVAL '1 minute'),
    ('test_msg_g2_4', 'test_conv_group_2', 'test_bob_456',     'Confirmed by QA. Deploying in 30 min.', 'text', NOW() - INTERVAL '20 seconds')
ON CONFLICT (id) DO NOTHING;

-- Set some messages as read by recipients
INSERT INTO message_receipts (message_id, user_id, delivered_at, read_at)
VALUES
    -- alice's messages read by bob
    ('test_msg_1', 'test_bob_456',     NOW() - INTERVAL '5 minutes', NOW() - INTERVAL '4 minutes'),
    ('test_msg_3', 'test_bob_456',     NOW(), NULL),

    -- group 1 messages read by members
    ('test_msg_g1_1', 'test_charlie_789', NOW() - INTERVAL '2 minutes', NOW() - INTERVAL '90 seconds'),
    ('test_msg_g1_1', 'test_diana_101',   NOW() - INTERVAL '100 seconds', NOW() - INTERVAL '95 seconds'),
    ('test_msg_g1_2', 'test_alice_123',   NOW() - INTERVAL '80 seconds', NOW() - INTERVAL '75 seconds'),

    -- group 2 messages read by members
    ('test_msg_g2_1', 'test_eve_202',     NOW() - INTERVAL '2 minutes', NOW() - INTERVAL '110 seconds'),
    ('test_msg_g2_2', 'test_bob_456',     NOW() - INTERVAL '90 seconds', NOW() - INTERVAL '85 seconds'),
    ('test_msg_g2_3', 'test_bob_456',     NOW() - INTERVAL '50 seconds', NOW() - INTERVAL '45 seconds')
ON CONFLICT (message_id, user_id) DO NOTHING;

-- Insert seed completion log
SELECT format('Seed data created: %s users, 3 conversations, 12 messages', COUNT(DISTINCT user_id))
FROM users WHERE id LIKE 'test_%';
