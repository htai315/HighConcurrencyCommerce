-- T20: PayOS payment-attempt hardening.
-- Safe to replay. Secrets and raw provider credentials are never stored here.

CREATE SEQUENCE IF NOT EXISTS store.payos_order_code_seq
    AS bigint
    MINVALUE 1
    MAXVALUE 9007199254740991
    START WITH 202611090000001
    NO CYCLE;

ALTER TABLE store.payment_attempts
    ADD COLUMN IF NOT EXISTS provider_payment_link_id varchar(80),
    ADD COLUMN IF NOT EXISTS last_error_code varchar(64),
    ADD COLUMN IF NOT EXISTS last_queried_at timestamptz;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'store.payment_attempts'::regclass
          AND conname = 'ck_payment_attempt_provider_order_code') THEN
        ALTER TABLE store.payment_attempts
            ADD CONSTRAINT ck_payment_attempt_provider_order_code
            CHECK (provider_order_code IS NULL OR provider_order_code BETWEEN 1 AND 9007199254740991);
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_payment_attempt_active
    ON store.payment_attempts(payment_id)
    WHERE attempt_status IN ('CREATED', 'LINK_READY');

CREATE UNIQUE INDEX IF NOT EXISTS ux_payment_attempt_provider_link
    ON store.payment_attempts(provider_payment_link_id)
    WHERE provider_payment_link_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_provider_callback_payload_hash
    ON store.provider_callback_events(payload_hash);

DO $$
DECLARE constraint_name text;
BEGIN
    SELECT c.conname INTO constraint_name
    FROM pg_constraint c
    WHERE c.conrelid = 'store.provider_callback_events'::regclass
      AND c.contype = 'c'
      AND pg_get_constraintdef(c.oid) ILIKE '%process_result%';

    IF constraint_name IS NOT NULL THEN
        EXECUTE format('ALTER TABLE store.provider_callback_events DROP CONSTRAINT %I', constraint_name);
    END IF;

    ALTER TABLE store.provider_callback_events
        ADD CONSTRAINT ck_provider_callback_process_result
        CHECK (process_result IN ('RECEIVED','APPLIED','DUPLICATE','REJECTED','LATE_PAID','ERROR'));
END $$;

