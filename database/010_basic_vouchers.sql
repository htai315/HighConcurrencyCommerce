-- 010_basic_vouchers.sql: Basic Vouchers (T38)
-- Additive upgrade after 009; safe to reapply without altering existing data.
BEGIN;

CREATE TABLE IF NOT EXISTS store.vouchers (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    code varchar(32) NOT NULL,
    name varchar(100) NOT NULL,
    voucher_status text NOT NULL DEFAULT 'DRAFT'
        CHECK (voucher_status IN ('DRAFT', 'ACTIVE', 'PAUSED', 'ENDED')),
    discount_type text NOT NULL
        CHECK (discount_type IN ('FIXED_AMOUNT', 'PERCENTAGE')),
    discount_value numeric(18,2) NOT NULL CHECK (discount_value > 0),
    max_discount_amount numeric(18,2) NULL,
    minimum_order_amount numeric(18,2) NOT NULL DEFAULT 0 CHECK (minimum_order_amount >= 0),
    total_usage_limit integer NOT NULL CHECK (total_usage_limit > 0),
    per_customer_limit integer NOT NULL CHECK (per_customer_limit > 0 AND per_customer_limit <= total_usage_limit),
    usage_held integer NOT NULL DEFAULT 0 CHECK (usage_held >= 0),
    usage_consumed integer NOT NULL DEFAULT 0 CHECK (usage_consumed >= 0),
    budget_total numeric(18,2) NOT NULL CHECK (budget_total > 0),
    budget_held numeric(18,2) NOT NULL DEFAULT 0 CHECK (budget_held >= 0),
    budget_consumed numeric(18,2) NOT NULL DEFAULT 0 CHECK (budget_consumed >= 0),
    starts_at timestamptz NOT NULL,
    ends_at timestamptz NOT NULL,
    activated_at timestamptz NULL,
    created_by uuid NOT NULL REFERENCES store.user_accounts(id),
    activated_by uuid NULL REFERENCES store.user_accounts(id),
    revision integer NOT NULL DEFAULT 0 CHECK (revision >= 0),
    CONSTRAINT ck_vouchers_dates CHECK (starts_at < ends_at),
    CONSTRAINT ck_vouchers_usage_limit CHECK (usage_held + usage_consumed <= total_usage_limit),
    CONSTRAINT ck_vouchers_budget_limit CHECK (budget_held + budget_consumed <= budget_total),
    CONSTRAINT ck_vouchers_code_format CHECK (code ~ '^[A-Z0-9_-]{3,32}$'),
    CONSTRAINT ck_vouchers_discount_config CHECK (
        (discount_type = 'FIXED_AMOUNT' AND max_discount_amount IS NULL AND discount_value = floor(discount_value) AND discount_value > 0)
        OR
        (discount_type = 'PERCENTAGE' AND discount_value BETWEEN 1 AND 99 AND discount_value = floor(discount_value)
            AND max_discount_amount IS NOT NULL AND max_discount_amount > 0 AND max_discount_amount = floor(max_discount_amount))
    ),
    CONSTRAINT ck_vouchers_currency CHECK (
        minimum_order_amount = floor(minimum_order_amount)
        AND budget_total = floor(budget_total)
        AND budget_held = floor(budget_held)
        AND budget_consumed = floor(budget_consumed)
    ),
    CONSTRAINT ck_vouchers_activation CHECK (
        (voucher_status = 'DRAFT' AND activated_at IS NULL AND activated_by IS NULL)
        OR
        (voucher_status IN ('ACTIVE', 'PAUSED', 'ENDED') AND activated_at IS NOT NULL AND activated_by IS NOT NULL)
    )
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'store'
          AND table_name = 'vouchers'
          AND column_name = 'name'
    ) THEN
        -- Add the column nullable first so databases that already contain vouchers
        -- can be upgraded without inventing an empty program name.
        ALTER TABLE store.vouchers ADD COLUMN name varchar(100);
    END IF;

    -- Older T38 installations used an empty-string default. Preserve every
    -- existing voucher while giving it a meaningful, deterministic name.
    UPDATE store.vouchers
    SET name = code
    WHERE name IS NULL OR btrim(name) = '';

    ALTER TABLE store.vouchers ALTER COLUMN name SET NOT NULL;
    ALTER TABLE store.vouchers ALTER COLUMN name DROP DEFAULT;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint c
        JOIN pg_class t ON t.oid = c.conrelid
        JOIN pg_namespace n ON n.oid = t.relnamespace
        WHERE c.conname = 'ck_vouchers_name_not_blank'
          AND n.nspname = 'store'
          AND t.relname = 'vouchers'
    ) THEN
        ALTER TABLE store.vouchers
            ADD CONSTRAINT ck_vouchers_name_not_blank
            CHECK (btrim(name) <> '') NOT VALID;
    END IF;

    ALTER TABLE store.vouchers
        VALIDATE CONSTRAINT ck_vouchers_name_not_blank;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_vouchers_upper_code
    ON store.vouchers(upper(code));

CREATE INDEX IF NOT EXISTS ix_vouchers_status_dates
    ON store.vouchers(voucher_status, starts_at, ends_at);

CREATE TABLE IF NOT EXISTS store.voucher_redemptions (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    voucher_id uuid NOT NULL REFERENCES store.vouchers(id),
    customer_id uuid NOT NULL REFERENCES store.user_accounts(id),
    order_id uuid NOT NULL REFERENCES store.sales_orders(id),
    subtotal_snapshot numeric(18,2) NOT NULL,
    discount_amount numeric(18,2) NOT NULL,
    redemption_status text NOT NULL DEFAULT 'HELD'
        CHECK (redemption_status IN ('HELD', 'CONSUMED', 'RELEASED')),
    held_at timestamptz NOT NULL DEFAULT now(),
    consumed_at timestamptz NULL,
    released_at timestamptz NULL,
    release_reason varchar(100) NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_voucher_redemptions_amounts CHECK (
        subtotal_snapshot > 0
        AND discount_amount > 0
        AND discount_amount < subtotal_snapshot
        AND discount_amount = floor(discount_amount)
        AND subtotal_snapshot = floor(subtotal_snapshot)
    ),
    CONSTRAINT ck_voucher_redemptions_status CHECK (
        (redemption_status = 'HELD' AND consumed_at IS NULL AND released_at IS NULL AND release_reason IS NULL)
        OR
        (redemption_status = 'CONSUMED' AND consumed_at IS NOT NULL AND released_at IS NULL AND release_reason IS NULL AND consumed_at >= held_at)
        OR
        (redemption_status = 'RELEASED' AND released_at IS NOT NULL AND release_reason IS NOT NULL AND released_at >= held_at)
    )
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_voucher_redemptions_order_id
    ON store.voucher_redemptions(order_id);

CREATE INDEX IF NOT EXISTS ix_voucher_redemptions_voucher_customer_status
    ON store.voucher_redemptions(voucher_id, customer_id, redemption_status);

CREATE INDEX IF NOT EXISTS ix_voucher_redemptions_voucher_status
    ON store.voucher_redemptions(voucher_id, redemption_status);

-- Snapshot columns on store.sales_orders
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'store' AND table_name = 'sales_orders' AND column_name = 'subtotal_amount'
    ) THEN
        ALTER TABLE store.sales_orders ADD COLUMN subtotal_amount numeric(18,2);
        UPDATE store.sales_orders SET subtotal_amount = total_amount WHERE subtotal_amount IS NULL;
        ALTER TABLE store.sales_orders ALTER COLUMN subtotal_amount SET NOT NULL;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'store' AND table_name = 'sales_orders' AND column_name = 'voucher_discount_amount'
    ) THEN
        ALTER TABLE store.sales_orders ADD COLUMN voucher_discount_amount numeric(18,2) NOT NULL DEFAULT 0;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'store' AND table_name = 'sales_orders' AND column_name = 'voucher_code_snapshot'
    ) THEN
        ALTER TABLE store.sales_orders ADD COLUMN voucher_code_snapshot varchar(32) NULL;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_sales_orders_subtotal_discount'
    ) THEN
        ALTER TABLE store.sales_orders
            ADD CONSTRAINT ck_sales_orders_subtotal_discount
            CHECK (
                total_amount = subtotal_amount - voucher_discount_amount
                AND voucher_discount_amount >= 0
                AND total_amount > 0
                AND subtotal_amount = floor(subtotal_amount)
                AND voucher_discount_amount = floor(voucher_discount_amount)
            );
    END IF;
END $$;

CREATE OR REPLACE FUNCTION store.trg_sales_orders_subtotal_default()
RETURNS trigger AS $$
BEGIN
    IF NEW.subtotal_amount IS NULL THEN
        NEW.subtotal_amount := NEW.total_amount;
    END IF;
    IF NEW.voucher_discount_amount IS NULL THEN
        NEW.voucher_discount_amount := 0;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_sales_orders_subtotal_default ON store.sales_orders;
CREATE TRIGGER trg_sales_orders_subtotal_default
    BEFORE INSERT ON store.sales_orders
    FOR EACH ROW
    EXECUTE FUNCTION store.trg_sales_orders_subtotal_default();

COMMIT;

