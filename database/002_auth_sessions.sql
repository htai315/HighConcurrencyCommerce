-- ADR 0001. Additive upgrade after 001; safe to reapply.
BEGIN;
CREATE TABLE IF NOT EXISTS store.auth_sessions (
    token_hash varchar(64) PRIMARY KEY,
    user_id uuid NOT NULL REFERENCES store.user_accounts(id) ON DELETE CASCADE,
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_auth_sessions_user ON store.auth_sessions(user_id);
CREATE INDEX IF NOT EXISTS ix_auth_sessions_expiry ON store.auth_sessions(expires_at);
INSERT INTO store.roles(role_key,display_name) VALUES
 ('CUSTOMER','Customer'),('STAFF','Staff'),('ADMIN','Admin')
ON CONFLICT (role_key) DO NOTHING;

CREATE OR REPLACE FUNCTION store.revoke_account_sessions() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.password_hash IS DISTINCT FROM OLD.password_hash
       OR NEW.role_id IS DISTINCT FROM OLD.role_id
       OR NEW.account_status IS DISTINCT FROM OLD.account_status THEN
        DELETE FROM store.auth_sessions WHERE user_id = NEW.id;
    END IF;
    RETURN NEW;
END;
$$;
CREATE OR REPLACE TRIGGER revoke_account_sessions
AFTER UPDATE OF password_hash, role_id, account_status ON store.user_accounts
FOR EACH ROW EXECUTE FUNCTION store.revoke_account_sessions();
COMMIT;

