-- =============================================================================
-- V001__create_users_and_conversations.sql
-- Real-Time Chat System — Custom types + users + conversations
--
-- ROLLBACK:
--   DROP TABLE IF EXISTS conversations CASCADE;
--   DROP TABLE IF EXISTS users CASCADE;
--   DROP TYPE IF EXISTS conv_type;
--   DROP TYPE IF EXISTS user_status;
-- =============================================================================

CREATE TYPE user_status AS ENUM ('active', 'suspended');
CREATE TYPE conv_type   AS ENUM ('direct', 'group');
CREATE TYPE msg_type    AS ENUM ('text', 'system');

CREATE TABLE users (
    id           VARCHAR(36)   NOT NULL,
    username     VARCHAR(64)   NOT NULL,
    email        VARCHAR(255)  NOT NULL,
    status       user_status   NOT NULL DEFAULT 'active',
    last_seen_at TIMESTAMPTZ   NULL,
    created_at   TIMESTAMPTZ   NOT NULL DEFAULT NOW(),

    CONSTRAINT users_pkey PRIMARY KEY (id),
    CONSTRAINT users_username_unique UNIQUE (username),
    CONSTRAINT users_email_unique UNIQUE (email)
);

COMMENT ON TABLE users IS
    'Chat user identities. last_seen_at updated on WebSocket disconnect. '
    'Users are suspended, never hard-deleted — hard delete would orphan message history.';

CREATE TABLE conversations (
    id          VARCHAR(36)   NOT NULL,
    type        conv_type     NOT NULL,
    name        VARCHAR(128)  NULL,
    created_by  VARCHAR(36)   NOT NULL,
    created_at  TIMESTAMPTZ   NOT NULL DEFAULT NOW(),

    CONSTRAINT conversations_pkey PRIMARY KEY (id),

    CONSTRAINT conversations_created_by_fk
        FOREIGN KEY (created_by) REFERENCES users (id)
        ON DELETE RESTRICT,

    -- Group conversations may have a name; direct conversations must not.
    CONSTRAINT conversations_group_name_check
        CHECK (type = 'group' OR name IS NULL)
);

COMMENT ON TABLE conversations IS
    'Container for messages. type=direct has exactly 2 members; type=group has 2–500.';
