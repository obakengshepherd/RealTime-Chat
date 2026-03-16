-- =============================================================================
-- V004__add_performance_constraints.sql
-- Real-Time Chat System — Additional constraints for data integrity
--
-- ROLLBACK: Drop constraints listed below individually.
-- =============================================================================

-- Enforce that a direct conversation has exactly 2 members
-- (checked at application layer; DB-level enforcement via trigger)
CREATE OR REPLACE FUNCTION check_direct_conversation_members()
RETURNS TRIGGER AS $$
DECLARE
    conv_type_val conv_type;
    member_count  INTEGER;
BEGIN
    SELECT type INTO conv_type_val FROM conversations WHERE id = NEW.conversation_id;
    IF conv_type_val = 'direct' THEN
        SELECT COUNT(*) INTO member_count
        FROM conversation_members
        WHERE conversation_id = NEW.conversation_id;
        IF member_count >= 2 THEN
            RAISE EXCEPTION 'Direct conversations cannot have more than 2 members.';
        END IF;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER enforce_direct_conversation_member_limit
    BEFORE INSERT ON conversation_members
    FOR EACH ROW
    EXECUTE FUNCTION check_direct_conversation_members();

COMMENT ON TRIGGER enforce_direct_conversation_member_limit ON conversation_members IS
    'Prevents adding a 3rd member to a direct conversation at the DB level.';
