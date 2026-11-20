-- 005_auth_email_recovery.sql: Password Reset & Email Verification (T33)
-- Additive upgrade after 004; safe to reapply without resetting verification state.
BEGIN;

-- 1. Add email_verified_at to user_accounts and backfill existing accounts ONLY upon initial creation.
-- Re-running this script must never verify pending unverified accounts.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'store'
          AND table_name = 'user_accounts'
          AND column_name = 'email_verified_at'
    ) THEN
        ALTER TABLE store.user_accounts ADD COLUMN email_verified_at timestamptz NULL;
        -- Backfill only pre-existing accounts that existed before this column was introduced.
        UPDATE store.user_accounts SET email_verified_at = now() WHERE email_verified_at IS NULL;
    END IF;
END $$;

-- 2. One-time action tokens table (PASSWORD_RESET, VERIFY_EMAIL)
CREATE TABLE IF NOT EXISTS store.auth_action_tokens (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id uuid NOT NULL REFERENCES store.user_accounts(id) ON DELETE CASCADE,
    purpose varchar(32) NOT NULL CHECK (purpose IN ('PASSWORD_RESET', 'VERIFY_EMAIL')),
    token_hash char(64) NOT NULL UNIQUE,
    email_snapshot varchar(320) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    consumed_at timestamptz NULL,
    revoked_at timestamptz NULL
);

CREATE INDEX IF NOT EXISTS ix_auth_action_tokens_user_purpose
    ON store.auth_action_tokens(user_id, purpose, expires_at);

CREATE INDEX IF NOT EXISTS ix_auth_action_tokens_cleanup
    ON store.auth_action_tokens(expires_at)
    WHERE consumed_at IS NULL AND revoked_at IS NULL;

-- 3. Outbox table for reliable asynchronous email delivery with lease/claim support
CREATE TABLE IF NOT EXISTS store.auth_email_outbox (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id uuid NULL REFERENCES store.user_accounts(id) ON DELETE SET NULL,
    action_token_id uuid NULL REFERENCES store.auth_action_tokens(id) ON DELETE SET NULL,
    template_key varchar(64) NOT NULL CHECK (template_key IN ('PASSWORD_RESET', 'VERIFY_EMAIL')),
    recipient_email varchar(320) NOT NULL,
    protected_payload text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    sent_at timestamptz NULL,
    discarded_at timestamptz NULL,
    attempt_count int NOT NULL DEFAULT 0,
    last_error_code varchar(64) NULL,
    locked_at timestamptz NULL,
    locked_by varchar(64) NULL
);

ALTER TABLE store.auth_email_outbox ADD COLUMN IF NOT EXISTS locked_at timestamptz NULL;
ALTER TABLE store.auth_email_outbox ADD COLUMN IF NOT EXISTS locked_by varchar(64) NULL;

CREATE INDEX IF NOT EXISTS ix_auth_email_outbox_pending
    ON store.auth_email_outbox(next_attempt_at, locked_at)
    WHERE sent_at IS NULL AND discarded_at IS NULL;

COMMIT;
