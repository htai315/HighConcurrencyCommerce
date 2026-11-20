-- 013_direct_product_exchange.sql: Direct SKU exchange and replacement shipment (T42)
BEGIN;

ALTER TABLE store.order_return_requests
    ADD COLUMN IF NOT EXISTS resolution_type varchar(16) NOT NULL DEFAULT 'RETURN';

ALTER TABLE store.order_return_requests DROP CONSTRAINT IF EXISTS ck_order_return_resolution_type;
ALTER TABLE store.order_return_requests
    ADD CONSTRAINT ck_order_return_resolution_type CHECK (resolution_type IN ('RETURN','EXCHANGE'));

ALTER TABLE store.order_return_requests DROP CONSTRAINT IF EXISTS ck_return_requests_lifecycle_v2;
DO $$
DECLARE constraint_name text;
BEGIN
    FOR constraint_name IN
        SELECT c.conname FROM pg_constraint c
        JOIN pg_class t ON t.oid=c.conrelid JOIN pg_namespace n ON n.oid=t.relnamespace
        WHERE n.nspname='store' AND t.relname='order_return_requests' AND c.contype='c'
          AND pg_get_constraintdef(c.oid) ILIKE '%return_status%'
    LOOP
        EXECUTE format('ALTER TABLE store.order_return_requests DROP CONSTRAINT %I', constraint_name);
    END LOOP;
END $$;

ALTER TABLE store.order_return_requests
    ADD CONSTRAINT ck_order_return_status CHECK (return_status IN (
        'REQUESTED','APPROVED','REJECTED','CANCELLED','RECEIVED','INSPECTED','REFUNDED',
        'EXCHANGE_READY','EXCHANGE_PACKED','EXCHANGE_SHIPPING','EXCHANGED',
        'EXCHANGE_EXPIRED','EXCHANGE_DELIVERY_FAILED')),
    ADD CONSTRAINT ck_order_return_resolution_status CHECK (
        (resolution_type='RETURN' AND return_status NOT LIKE 'EXCHANGE_%' AND return_status <> 'EXCHANGED')
        OR
        (resolution_type='EXCHANGE' AND return_status <> 'REFUNDED')
    );

ALTER TABLE store.order_return_requests
    ADD CONSTRAINT ck_return_requests_lifecycle_v3 CHECK (
        (return_status = 'REQUESTED' AND decided_by IS NULL AND decided_at IS NULL AND received_by IS NULL AND received_at IS NULL AND inspected_by IS NULL AND inspected_at IS NULL)
        OR (return_status = 'CANCELLED' AND decided_by IS NULL AND decided_at IS NULL AND received_by IS NULL AND received_at IS NULL AND inspected_by IS NULL AND inspected_at IS NULL)
        OR (return_status = 'REJECTED' AND decided_by IS NOT NULL AND decided_at IS NOT NULL AND received_by IS NULL AND received_at IS NULL AND inspected_by IS NULL AND inspected_at IS NULL)
        OR (return_status = 'APPROVED' AND decided_by IS NOT NULL AND decided_at IS NOT NULL AND received_by IS NULL AND received_at IS NULL AND inspected_by IS NULL AND inspected_at IS NULL)
        OR (return_status = 'RECEIVED' AND decided_by IS NOT NULL AND decided_at IS NOT NULL AND received_by IS NOT NULL AND received_at IS NOT NULL AND inspected_by IS NULL AND inspected_at IS NULL)
        OR (return_status = 'REFUNDED' AND decided_by IS NOT NULL AND decided_at IS NOT NULL AND received_by IS NOT NULL AND received_at IS NOT NULL)
        OR (return_status IN ('INSPECTED','EXCHANGE_READY','EXCHANGE_PACKED','EXCHANGE_SHIPPING','EXCHANGED','EXCHANGE_DELIVERY_FAILED')
            AND decided_by IS NOT NULL AND decided_at IS NOT NULL AND received_by IS NOT NULL AND received_at IS NOT NULL
            AND inspected_by IS NOT NULL AND inspected_at IS NOT NULL AND inspected_at >= received_at)
        OR (return_status = 'EXCHANGE_EXPIRED' AND decided_by IS NOT NULL AND decided_at IS NOT NULL AND received_by IS NULL AND received_at IS NULL AND inspected_by IS NULL AND inspected_at IS NULL)
    );

CREATE TABLE IF NOT EXISTS store.order_exchange_lines (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    return_request_id uuid NOT NULL REFERENCES store.order_return_requests(id) ON DELETE CASCADE,
    return_request_line_id uuid NOT NULL UNIQUE REFERENCES store.order_return_request_lines(id) ON DELETE CASCADE,
    source_sku_id uuid NOT NULL REFERENCES store.product_skus(id),
    target_sku_id uuid NOT NULL REFERENCES store.product_skus(id),
    quantity integer NOT NULL CHECK (quantity > 0),
    target_sku_code_snapshot varchar(64) NOT NULL,
    target_option_label_snapshot varchar(100) NOT NULL,
    target_regular_price_snapshot numeric(18,2) NOT NULL CHECK (target_regular_price_snapshot >= 0 AND target_regular_price_snapshot=floor(target_regular_price_snapshot)),
    CONSTRAINT ck_exchange_line_different_sku CHECK (source_sku_id <> target_sku_id),
    CONSTRAINT ux_exchange_line_return_target UNIQUE(return_request_id,target_sku_id)
);

CREATE INDEX IF NOT EXISTS ix_order_exchange_lines_return ON store.order_exchange_lines(return_request_id);

CREATE TABLE IF NOT EXISTS store.exchange_stock_allocations (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    return_request_id uuid NOT NULL REFERENCES store.order_return_requests(id) ON DELETE CASCADE,
    exchange_line_id uuid NOT NULL UNIQUE REFERENCES store.order_exchange_lines(id) ON DELETE CASCADE,
    product_sku_id uuid NOT NULL REFERENCES store.product_skus(id),
    quantity integer NOT NULL CHECK (quantity > 0),
    allocation_status varchar(16) NOT NULL DEFAULT 'HELD' CHECK (allocation_status IN ('HELD','ALLOCATED','RELEASED','CONSUMED')),
    reserved_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    released_at timestamptz NULL,
    release_reason varchar(100) NULL,
    revision integer NOT NULL DEFAULT 0 CHECK (revision >= 0),
    CONSTRAINT ck_exchange_allocation_expiry CHECK (expires_at > reserved_at),
    CONSTRAINT ux_exchange_allocation_return_sku UNIQUE(return_request_id,product_sku_id)
);

CREATE INDEX IF NOT EXISTS ix_exchange_allocations_expiry
    ON store.exchange_stock_allocations(expires_at,id) WHERE allocation_status='HELD';

CREATE TABLE IF NOT EXISTS store.exchange_shipments (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    return_request_id uuid NOT NULL UNIQUE REFERENCES store.order_return_requests(id),
    shipment_status varchar(24) NOT NULL DEFAULT 'PENDING_PACKING'
        CHECK (shipment_status IN ('PENDING_PACKING','PACKED','SHIPPING','DELIVERED','DELIVERY_FAILED')),
    redelivery_count smallint NOT NULL DEFAULT 0 CHECK (redelivery_count BETWEEN 0 AND 2),
    return_stock_completed boolean NOT NULL DEFAULT false,
    shipped_at timestamptz NULL,
    delivered_at timestamptz NULL
);

CREATE TABLE IF NOT EXISTS store.exchange_delivery_attempts (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    exchange_shipment_id uuid NOT NULL REFERENCES store.exchange_shipments(id) ON DELETE CASCADE,
    attempt_no smallint NOT NULL CHECK (attempt_no BETWEEN 1 AND 3),
    result varchar(16) NOT NULL CHECK (result IN ('IN_PROGRESS','DELIVERED','FAILED')),
    note text NULL,
    finished_at timestamptz NULL,
    CONSTRAINT ux_exchange_delivery_attempt UNIQUE(exchange_shipment_id,attempt_no)
);

DO $$
DECLARE constraint_name text;
BEGIN
    FOR constraint_name IN
        SELECT c.conname FROM pg_constraint c
        JOIN pg_class t ON t.oid=c.conrelid JOIN pg_namespace n ON n.oid=t.relnamespace
        WHERE n.nspname='store' AND t.relname='inventory_movements' AND c.contype='c'
          AND pg_get_constraintdef(c.oid) ILIKE '%movement_type%'
    LOOP
        EXECUTE format('ALTER TABLE store.inventory_movements DROP CONSTRAINT %I', constraint_name);
    END LOOP;
END $$;

ALTER TABLE store.inventory_movements ADD CONSTRAINT ck_inventory_movement_type CHECK (movement_type IN (
    'STOCK_IN','RESERVE','RELEASE','ALLOCATE','STOCK_OUT','RETURN_IN',
    'RETURN_RESTOCK','RETURN_DAMAGED','RETURN_SCRAP','ADJUST',
    'EXCHANGE_RESERVE','EXCHANGE_RELEASE','EXCHANGE_ALLOCATE','EXCHANGE_STOCK_OUT','EXCHANGE_RETURN_IN'));

COMMIT;
