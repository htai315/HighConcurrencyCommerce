-- 006_internal_staff_invites.sql: Internal Account & Staff Management (T34)
-- Additive upgrade after 005; safe to reapply without altering existing accounts or tokens.
BEGIN;

DO $$
DECLARE
    r RECORD;
BEGIN
    -- 1. Extend auth_action_tokens.purpose check constraint to include 'STAFF_INVITE'
    FOR r IN (
        SELECT conname
        FROM pg_constraint c
        JOIN pg_class t ON c.conrelid = t.oid
        JOIN pg_namespace n ON t.relnamespace = n.oid
        WHERE n.nspname = 'store'
          AND t.relname = 'auth_action_tokens'
          AND c.contype = 'c'
          AND pg_get_constraintdef(c.oid) LIKE '%purpose%'
    ) LOOP
        EXECUTE 'ALTER TABLE store.auth_action_tokens DROP CONSTRAINT IF EXISTS ' || quote_ident(r.conname);
    END LOOP;

    ALTER TABLE store.auth_action_tokens
        ADD CONSTRAINT ck_auth_action_tokens_purpose
        CHECK (purpose IN ('PASSWORD_RESET', 'VERIFY_EMAIL', 'STAFF_INVITE'));

    -- 2. Extend auth_email_outbox.template_key check constraint to include 'STAFF_INVITE'
    FOR r IN (
        SELECT conname
        FROM pg_constraint c
        JOIN pg_class t ON c.conrelid = t.oid
        JOIN pg_namespace n ON t.relnamespace = n.oid
        WHERE n.nspname = 'store'
          AND t.relname = 'auth_email_outbox'
          AND c.contype = 'c'
          AND pg_get_constraintdef(c.oid) LIKE '%template_key%'
    ) LOOP
        EXECUTE 'ALTER TABLE store.auth_email_outbox DROP CONSTRAINT IF EXISTS ' || quote_ident(r.conname);
    END LOOP;

    ALTER TABLE store.auth_email_outbox
        ADD CONSTRAINT ck_auth_email_outbox_template_key
        CHECK (template_key IN ('PASSWORD_RESET', 'VERIFY_EMAIL', 'STAFF_INVITE'));
END $$;

-- 3. Composite indexes for admin account listing, filtering, and deterministic pagination
CREATE INDEX IF NOT EXISTS ix_user_accounts_role_status_created
    ON store.user_accounts(role_id, account_status, created_at DESC, id DESC);

CREATE INDEX IF NOT EXISTS ix_user_accounts_created_id
    ON store.user_accounts(created_at DESC, id DESC);

COMMIT;

