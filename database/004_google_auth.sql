-- 004_google_auth.sql: Google Authentication & External Identities
-- Additive upgrade after 003; safe to reapply.
BEGIN;

-- 1. Allow password_hash to be NULL for Google-only users
ALTER TABLE store.user_accounts
    ALTER COLUMN password_hash DROP NOT NULL;

-- 2. External identities table
CREATE TABLE IF NOT EXISTS store.external_identities (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id uuid NOT NULL REFERENCES store.user_accounts(id) ON DELETE CASCADE,
    provider varchar(32) NOT NULL,
    subject varchar(255) NOT NULL,
    email varchar(320) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_external_identities_provider_subject UNIQUE (provider, subject),
    CONSTRAINT uq_external_identities_user_provider UNIQUE (user_id, provider)
);

CREATE INDEX IF NOT EXISTS ix_external_identities_user_id
    ON store.external_identities(user_id);

CREATE INDEX IF NOT EXISTS ix_external_identities_provider_subject
    ON store.external_identities(provider, subject);

-- 3. Auth challenges table for nonce / challenge validation & replay prevention
CREATE TABLE IF NOT EXISTS store.auth_challenges (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    nonce varchar(64) NOT NULL,
    purpose varchar(32) NOT NULL,
    user_id uuid NULL REFERENCES store.user_accounts(id) ON DELETE CASCADE,
    token_hash varchar(64) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    consumed_at timestamptz NULL,
    CONSTRAINT uq_auth_challenges_nonce UNIQUE (nonce)
);

CREATE INDEX IF NOT EXISTS ix_auth_challenges_lookup
    ON store.auth_challenges(id, purpose, expires_at);

CREATE INDEX IF NOT EXISTS ix_auth_challenges_expiry
    ON store.auth_challenges(expires_at);

COMMIT;

