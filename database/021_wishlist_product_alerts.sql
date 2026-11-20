-- 021_wishlist_product_alerts.sql: durable Wishlist product-alert foundation (T51.1)
-- Trigger functions only mark a product dirty. Business evaluation and email
-- delivery remain outside inventory/product transactions.
BEGIN;

ALTER TABLE store.customer_wishlist_items
    ADD COLUMN IF NOT EXISTS back_in_stock_enabled boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS price_drop_enabled boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS email_enabled boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS last_observed_in_stock boolean,
    ADD COLUMN IF NOT EXISTS last_observed_min_price numeric(18,2),
    ADD COLUMN IF NOT EXISTS alert_sequence bigint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS alert_revision integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS last_evaluated_at timestamptz,
    ADD COLUMN IF NOT EXISTS last_notified_at timestamptz,
    ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'store.customer_wishlist_items'::regclass
          AND conname = 'ck_customer_wishlist_alert_channel') THEN
        ALTER TABLE store.customer_wishlist_items
            ADD CONSTRAINT ck_customer_wishlist_alert_channel CHECK (
                NOT email_enabled OR back_in_stock_enabled OR price_drop_enabled
            );
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'store.customer_wishlist_items'::regclass
          AND conname = 'ck_customer_wishlist_alert_observed_price') THEN
        ALTER TABLE store.customer_wishlist_items
            ADD CONSTRAINT ck_customer_wishlist_alert_observed_price CHECK (
                last_observed_min_price IS NULL OR last_observed_min_price >= 0
            );
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'store.customer_wishlist_items'::regclass
          AND conname = 'ck_customer_wishlist_alert_counters') THEN
        ALTER TABLE store.customer_wishlist_items
            ADD CONSTRAINT ck_customer_wishlist_alert_counters CHECK (
                alert_sequence >= 0 AND alert_revision >= 0
            );
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_customer_wishlist_alert_subscribers
    ON store.customer_wishlist_items(product_id, customer_id)
    WHERE back_in_stock_enabled OR price_drop_enabled;

CREATE TABLE IF NOT EXISTS store.product_alert_refresh_queue (
    product_id uuid PRIMARY KEY REFERENCES store.products(id) ON DELETE CASCADE,
    reason_mask integer NOT NULL,
    event_version bigint NOT NULL DEFAULT 1,
    queued_at timestamptz NOT NULL DEFAULT now(),
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    attempt_count integer NOT NULL DEFAULT 0,
    locked_at timestamptz,
    locked_by varchar(64),
    last_error_code varchar(64),
    CONSTRAINT ck_product_alert_queue_reason CHECK (reason_mask BETWEEN 1 AND 7),
    CONSTRAINT ck_product_alert_queue_version CHECK (event_version >= 1),
    CONSTRAINT ck_product_alert_queue_attempt CHECK (attempt_count >= 0),
    CONSTRAINT ck_product_alert_queue_lease CHECK (
        (locked_at IS NULL AND locked_by IS NULL)
        OR (locked_at IS NOT NULL AND locked_by IS NOT NULL AND btrim(locked_by) <> '')
    )
);

CREATE INDEX IF NOT EXISTS ix_product_alert_refresh_claim
    ON store.product_alert_refresh_queue(next_attempt_at, locked_at, queued_at, product_id);

CREATE TABLE IF NOT EXISTS store.commerce_email_outbox (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    recipient_id uuid NOT NULL REFERENCES store.user_accounts(id) ON DELETE RESTRICT,
    recipient_email varchar(320) NOT NULL,
    event_key varchar(180) NOT NULL,
    template_key varchar(64) NOT NULL,
    subject varchar(200) NOT NULL,
    payload jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    attempt_count integer NOT NULL DEFAULT 0,
    locked_at timestamptz,
    locked_by varchar(64),
    sent_at timestamptz,
    discarded_at timestamptz,
    last_error_code varchar(64),
    CONSTRAINT ux_commerce_email_outbox_event UNIQUE (recipient_id, event_key),
    CONSTRAINT ck_commerce_email_recipient CHECK (btrim(recipient_email) <> ''),
    CONSTRAINT ck_commerce_email_event_key CHECK (btrim(event_key) <> ''),
    CONSTRAINT ck_commerce_email_template CHECK (btrim(template_key) <> ''),
    CONSTRAINT ck_commerce_email_subject CHECK (btrim(subject) <> ''),
    CONSTRAINT ck_commerce_email_payload CHECK (jsonb_typeof(payload) = 'object'),
    CONSTRAINT ck_commerce_email_attempt CHECK (attempt_count >= 0),
    CONSTRAINT ck_commerce_email_lease CHECK (
        (locked_at IS NULL AND locked_by IS NULL)
        OR (locked_at IS NOT NULL AND locked_by IS NOT NULL AND btrim(locked_by) <> '')
    ),
    CONSTRAINT ck_commerce_email_terminal CHECK (sent_at IS NULL OR discarded_at IS NULL)
);

CREATE INDEX IF NOT EXISTS ix_commerce_email_outbox_claim
    ON store.commerce_email_outbox(next_attempt_at, locked_at, created_at, id)
    WHERE sent_at IS NULL AND discarded_at IS NULL;

ALTER TABLE store.in_app_notifications
    ADD COLUMN IF NOT EXISTS action_url varchar(500);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'store.in_app_notifications'::regclass
          AND conname = 'ck_in_app_notifications_action_url') THEN
        ALTER TABLE store.in_app_notifications
            ADD CONSTRAINT ck_in_app_notifications_action_url CHECK (
                action_url IS NULL
                OR (
                    action_url LIKE '/%'
                    AND action_url NOT LIKE '//%'
                    AND action_url !~ '[[:space:]]'
                )
            );
    END IF;
END $$;

-- reason bits: INVENTORY=1, PRICE=2, LIFECYCLE=4.
CREATE OR REPLACE FUNCTION store.enqueue_product_alert_refresh(
    target_product_id uuid,
    target_reason_mask integer)
RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    IF target_product_id IS NULL OR target_reason_mask NOT BETWEEN 1 AND 7 THEN
        RETURN;
    END IF;

    INSERT INTO store.product_alert_refresh_queue (
        product_id, reason_mask, event_version, queued_at, next_attempt_at)
    VALUES (target_product_id, target_reason_mask, 1, now(), now())
    ON CONFLICT (product_id) DO UPDATE SET
        reason_mask = store.product_alert_refresh_queue.reason_mask | EXCLUDED.reason_mask,
        event_version = store.product_alert_refresh_queue.event_version + 1,
        queued_at = EXCLUDED.queued_at,
        next_attempt_at = EXCLUDED.next_attempt_at,
        attempt_count = 0,
        last_error_code = NULL;
END;
$$;

CREATE OR REPLACE FUNCTION store.queue_product_alert_from_inventory()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    target_sku_id uuid;
    target_product_id uuid;
BEGIN
    IF TG_OP = 'UPDATE' AND NEW.available_qty IS NOT DISTINCT FROM OLD.available_qty THEN
        RETURN NEW;
    END IF;

    target_sku_id := CASE WHEN TG_OP = 'DELETE' THEN OLD.product_sku_id ELSE NEW.product_sku_id END;
    SELECT product_id INTO target_product_id
    FROM store.product_skus
    WHERE id = target_sku_id;

    PERFORM store.enqueue_product_alert_refresh(target_product_id, 1);
    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END;
$$;

CREATE OR REPLACE FUNCTION store.queue_product_alert_from_sku()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    old_product_id uuid;
    new_product_id uuid;
    reason integer := 0;
BEGIN
    old_product_id := CASE WHEN TG_OP = 'INSERT' THEN NULL ELSE OLD.product_id END;
    new_product_id := CASE WHEN TG_OP = 'DELETE' THEN NULL ELSE NEW.product_id END;

    IF TG_OP <> 'UPDATE' OR NEW.regular_price IS DISTINCT FROM OLD.regular_price THEN
        reason := reason | 2;
    END IF;
    IF TG_OP <> 'UPDATE'
       OR NEW.sku_status IS DISTINCT FROM OLD.sku_status
       OR NEW.product_id IS DISTINCT FROM OLD.product_id THEN
        reason := reason | 4;
    END IF;

    IF old_product_id IS NOT NULL THEN
        PERFORM store.enqueue_product_alert_refresh(old_product_id, reason);
    END IF;
    IF new_product_id IS NOT NULL AND new_product_id IS DISTINCT FROM old_product_id THEN
        PERFORM store.enqueue_product_alert_refresh(new_product_id, reason);
    END IF;

    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END;
$$;

CREATE OR REPLACE FUNCTION store.queue_product_alert_from_product()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF NEW.publication_status IS DISTINCT FROM OLD.publication_status THEN
        PERFORM store.enqueue_product_alert_refresh(NEW.id, 4);
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_product_alert_inventory ON store.inventory_balances;
CREATE TRIGGER trg_product_alert_inventory
AFTER INSERT OR DELETE OR UPDATE OF available_qty
ON store.inventory_balances
FOR EACH ROW EXECUTE FUNCTION store.queue_product_alert_from_inventory();

DROP TRIGGER IF EXISTS trg_product_alert_sku ON store.product_skus;
CREATE TRIGGER trg_product_alert_sku
AFTER INSERT OR DELETE OR UPDATE OF regular_price, sku_status, product_id
ON store.product_skus
FOR EACH ROW EXECUTE FUNCTION store.queue_product_alert_from_sku();

DROP TRIGGER IF EXISTS trg_product_alert_product ON store.products;
CREATE TRIGGER trg_product_alert_product
AFTER UPDATE OF publication_status
ON store.products
FOR EACH ROW EXECUTE FUNCTION store.queue_product_alert_from_product();

COMMIT;
