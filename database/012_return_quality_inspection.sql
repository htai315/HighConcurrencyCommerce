-- 012_return_quality_inspection.sql: Returned-goods inspection and quarantine (T41)
-- Additive/idempotent upgrade after 011.
BEGIN;

ALTER TABLE store.inventory_balances
    ADD COLUMN IF NOT EXISTS quarantined_qty integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS damaged_qty integer NOT NULL DEFAULT 0;

ALTER TABLE store.inventory_balances DROP CONSTRAINT IF EXISTS ck_inventory_nonnegative;
ALTER TABLE store.inventory_balances DROP CONSTRAINT IF EXISTS ck_inventory_partition;
ALTER TABLE store.inventory_balances
    ADD CONSTRAINT ck_inventory_nonnegative CHECK (
        physical_qty >= 0 AND available_qty >= 0 AND reserved_qty >= 0
        AND allocated_qty >= 0 AND quarantined_qty >= 0 AND damaged_qty >= 0
    ),
    ADD CONSTRAINT ck_inventory_partition CHECK (
        physical_qty = available_qty + reserved_qty + allocated_qty + quarantined_qty + damaged_qty
    );

ALTER TABLE store.order_return_requests
    ADD COLUMN IF NOT EXISTS inspected_by uuid NULL REFERENCES store.user_accounts(id),
    ADD COLUMN IF NOT EXISTS inspected_at timestamptz NULL,
    ADD COLUMN IF NOT EXISTS inspection_note varchar(500) NULL;

ALTER TABLE store.order_return_request_lines
    ADD COLUMN IF NOT EXISTS restock_qty integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS quarantine_qty integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS damaged_qty integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS scrapped_qty integer NOT NULL DEFAULT 0;

ALTER TABLE store.order_return_request_lines DROP CONSTRAINT IF EXISTS ck_return_line_inspection_quantities;
ALTER TABLE store.order_return_request_lines
    ADD CONSTRAINT ck_return_line_inspection_quantities CHECK (
        restock_qty >= 0 AND quarantine_qty >= 0 AND damaged_qty >= 0 AND scrapped_qty >= 0
        AND restock_qty + quarantine_qty + damaged_qty + scrapped_qty <= requested_qty
    );

-- Replace the unnamed status check created by 011, as well as this migration's
-- named constraint when the script is safely replayed.
ALTER TABLE store.order_return_requests DROP CONSTRAINT IF EXISTS ck_return_requests_lifecycle_v2;
DO $$
DECLARE constraint_name text;
BEGIN
    FOR constraint_name IN
        SELECT c.conname
        FROM pg_constraint c
        JOIN pg_class t ON t.oid = c.conrelid
        JOIN pg_namespace n ON n.oid = t.relnamespace
        WHERE n.nspname = 'store'
          AND t.relname = 'order_return_requests'
          AND c.contype = 'c'
          AND pg_get_constraintdef(c.oid) ILIKE '%return_status%'
    LOOP
        EXECUTE format('ALTER TABLE store.order_return_requests DROP CONSTRAINT %I', constraint_name);
    END LOOP;
END $$;

ALTER TABLE store.order_return_requests
    ADD CONSTRAINT ck_order_return_status CHECK (
        return_status IN ('REQUESTED','APPROVED','REJECTED','CANCELLED','RECEIVED','INSPECTED','REFUNDED')
    ),
    ADD CONSTRAINT ck_return_requests_lifecycle_v2 CHECK (
        (return_status = 'REQUESTED' AND decided_by IS NULL AND decided_at IS NULL AND received_by IS NULL AND received_at IS NULL AND inspected_by IS NULL AND inspected_at IS NULL)
        OR (return_status = 'CANCELLED' AND decided_by IS NULL AND decided_at IS NULL AND received_by IS NULL AND received_at IS NULL AND inspected_by IS NULL AND inspected_at IS NULL)
        OR (return_status = 'REJECTED' AND decided_by IS NOT NULL AND decided_at IS NOT NULL AND received_by IS NULL AND received_at IS NULL AND inspected_by IS NULL AND inspected_at IS NULL)
        OR (return_status = 'APPROVED' AND decided_by IS NOT NULL AND decided_at IS NOT NULL AND received_by IS NULL AND received_at IS NULL AND inspected_by IS NULL AND inspected_at IS NULL)
        OR (return_status = 'RECEIVED' AND decided_by IS NOT NULL AND decided_at IS NOT NULL AND received_by IS NOT NULL AND received_at IS NOT NULL AND inspected_by IS NULL AND inspected_at IS NULL)
        OR (return_status = 'INSPECTED' AND decided_by IS NOT NULL AND decided_at IS NOT NULL AND received_by IS NOT NULL AND received_at IS NOT NULL AND inspected_by IS NOT NULL AND inspected_at IS NOT NULL AND inspected_at >= received_at)
        OR (return_status = 'REFUNDED' AND decided_by IS NOT NULL AND decided_at IS NOT NULL AND received_by IS NOT NULL AND received_at IS NOT NULL)
    );

-- Remove the old lifecycle constraint after the replacement is in place.
ALTER TABLE store.order_return_requests DROP CONSTRAINT IF EXISTS ck_return_requests_lifecycle;

DO $$
DECLARE constraint_name text;
BEGIN
    FOR constraint_name IN
        SELECT c.conname
        FROM pg_constraint c
        JOIN pg_class t ON t.oid = c.conrelid
        JOIN pg_namespace n ON n.oid = t.relnamespace
        WHERE n.nspname = 'store'
          AND t.relname = 'inventory_movements'
          AND c.contype = 'c'
          AND pg_get_constraintdef(c.oid) ILIKE '%movement_type%'
    LOOP
        EXECUTE format('ALTER TABLE store.inventory_movements DROP CONSTRAINT %I', constraint_name);
    END LOOP;
END $$;

ALTER TABLE store.inventory_movements
    ADD CONSTRAINT ck_inventory_movement_type CHECK (
        movement_type IN ('STOCK_IN','RESERVE','RELEASE','ALLOCATE','STOCK_OUT','RETURN_IN','RETURN_RESTOCK','RETURN_DAMAGED','RETURN_SCRAP','ADJUST')
    );

CREATE INDEX IF NOT EXISTS ix_order_return_requests_inspection_queue
    ON store.order_return_requests(return_status, received_at, id)
    WHERE return_status IN ('RECEIVED','INSPECTED');

COMMIT;
