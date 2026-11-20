BEGIN;

CREATE TABLE IF NOT EXISTS store.shipping_methods (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    code varchar(32) NOT NULL UNIQUE,
    name varchar(120) NOT NULL,
    description varchar(500),
    method_status varchar(20) NOT NULL DEFAULT 'INACTIVE',
    base_fee numeric(18,2) NOT NULL DEFAULT 0,
    free_shipping_threshold numeric(18,2),
    estimated_min_days smallint NOT NULL,
    estimated_max_days smallint NOT NULL,
    is_default boolean NOT NULL DEFAULT false,
    display_order integer NOT NULL DEFAULT 0,
    revision bigint NOT NULL DEFAULT 1,
    created_by uuid NOT NULL,
    updated_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT shipping_methods_created_by_fkey FOREIGN KEY (created_by) REFERENCES store.user_accounts(id) ON DELETE RESTRICT,
    CONSTRAINT shipping_methods_updated_by_fkey FOREIGN KEY (updated_by) REFERENCES store.user_accounts(id) ON DELETE RESTRICT,
    CONSTRAINT ck_shipping_methods_code CHECK (code ~ '^[A-Z0-9_-]{3,32}$'),
    CONSTRAINT ck_shipping_methods_name CHECK (btrim(name) <> ''),
    CONSTRAINT ck_shipping_methods_status CHECK (method_status IN ('ACTIVE','INACTIVE')),
    CONSTRAINT ck_shipping_methods_money CHECK (
        base_fee >= 0 AND base_fee = floor(base_fee)
        AND (free_shipping_threshold IS NULL OR (free_shipping_threshold > 0 AND free_shipping_threshold = floor(free_shipping_threshold)))
    ),
    CONSTRAINT ck_shipping_methods_eta CHECK (
        estimated_min_days >= 0 AND estimated_max_days >= estimated_min_days AND estimated_max_days <= 365
    ),
    CONSTRAINT ck_shipping_methods_default_active CHECK (NOT is_default OR method_status = 'ACTIVE'),
    CONSTRAINT ck_shipping_methods_order_revision CHECK (display_order >= 0 AND revision >= 1)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_shipping_methods_one_active_default
    ON store.shipping_methods(is_default)
    WHERE is_default AND method_status = 'ACTIVE';

CREATE INDEX IF NOT EXISTS ix_shipping_methods_list
    ON store.shipping_methods(method_status, display_order, id);

CREATE TABLE IF NOT EXISTS store.shipping_method_province_rates (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    shipping_method_id uuid NOT NULL,
    province_key varchar(160) NOT NULL,
    surcharge_fee numeric(18,2) NOT NULL DEFAULT 0,
    is_active boolean NOT NULL DEFAULT true,
    created_by uuid NOT NULL,
    updated_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT shipping_method_province_rates_method_id_fkey FOREIGN KEY (shipping_method_id) REFERENCES store.shipping_methods(id) ON DELETE RESTRICT,
    CONSTRAINT shipping_method_province_rates_created_by_fkey FOREIGN KEY (created_by) REFERENCES store.user_accounts(id) ON DELETE RESTRICT,
    CONSTRAINT shipping_method_province_rates_updated_by_fkey FOREIGN KEY (updated_by) REFERENCES store.user_accounts(id) ON DELETE RESTRICT,
    CONSTRAINT ux_shipping_method_province_rate UNIQUE (shipping_method_id, province_key),
    CONSTRAINT ck_shipping_method_province_key CHECK (btrim(province_key) <> '' AND province_key = upper(province_key)),
    CONSTRAINT ck_shipping_method_province_money CHECK (surcharge_fee >= 0 AND surcharge_fee = floor(surcharge_fee))
);

CREATE INDEX IF NOT EXISTS ix_shipping_method_province_rates_lookup
    ON store.shipping_method_province_rates(shipping_method_id, province_key)
    WHERE is_active;

ALTER TABLE store.sales_orders
    ADD COLUMN IF NOT EXISTS shipping_method_id uuid,
    ADD COLUMN IF NOT EXISTS shipping_method_code_snapshot varchar(32),
    ADD COLUMN IF NOT EXISTS shipping_method_name_snapshot varchar(120),
    ADD COLUMN IF NOT EXISTS shipping_fee numeric(18,2),
    ADD COLUMN IF NOT EXISTS shipping_estimated_min_days smallint,
    ADD COLUMN IF NOT EXISTS shipping_estimated_max_days smallint,
    ADD COLUMN IF NOT EXISTS shipping_destination_province_key varchar(160),
    ADD COLUMN IF NOT EXISTS shipping_method_revision bigint;

UPDATE store.sales_orders
SET shipping_method_code_snapshot = COALESCE(shipping_method_code_snapshot, 'LEGACY_FREE'),
    shipping_method_name_snapshot = COALESCE(shipping_method_name_snapshot, 'Miễn phí (đơn lịch sử)'),
    shipping_fee = COALESCE(shipping_fee, 0)
WHERE shipping_method_code_snapshot IS NULL
   OR shipping_method_name_snapshot IS NULL
   OR shipping_fee IS NULL;

ALTER TABLE store.sales_orders
    ALTER COLUMN shipping_method_code_snapshot SET DEFAULT 'LEGACY_FREE',
    ALTER COLUMN shipping_method_code_snapshot SET NOT NULL,
    ALTER COLUMN shipping_method_name_snapshot SET DEFAULT 'Miễn phí (đơn lịch sử)',
    ALTER COLUMN shipping_method_name_snapshot SET NOT NULL,
    ALTER COLUMN shipping_fee SET DEFAULT 0,
    ALTER COLUMN shipping_fee SET NOT NULL;

DO $$ BEGIN
    ALTER TABLE store.sales_orders
        ADD CONSTRAINT sales_orders_shipping_method_id_fkey
        FOREIGN KEY (shipping_method_id) REFERENCES store.shipping_methods(id) ON DELETE RESTRICT;
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

ALTER TABLE store.sales_orders DROP CONSTRAINT IF EXISTS ck_sales_orders_subtotal_discount;
ALTER TABLE store.sales_orders DROP CONSTRAINT IF EXISTS ck_sales_orders_shipping_amounts;
ALTER TABLE store.sales_orders
    ADD CONSTRAINT ck_sales_orders_shipping_amounts CHECK (
        total_amount = subtotal_amount - voucher_discount_amount + shipping_fee
        AND subtotal_amount >= 0
        AND voucher_discount_amount >= 0
        AND shipping_fee >= 0
        AND total_amount > 0
        AND subtotal_amount = floor(subtotal_amount)
        AND voucher_discount_amount = floor(voucher_discount_amount)
        AND shipping_fee = floor(shipping_fee)
        AND (
            (shipping_estimated_min_days IS NULL AND shipping_estimated_max_days IS NULL)
            OR (shipping_estimated_min_days >= 0 AND shipping_estimated_max_days >= shipping_estimated_min_days)
        )
        AND (shipping_method_revision IS NULL OR shipping_method_revision >= 1)
    );

COMMIT;
