-- 011_cod_returns_refunds.sql: COD Returns & Manual Refunds (T39)
-- Additive upgrade after 010; safe to reapply without altering existing data.
BEGIN;

-- 1. Table: store.order_return_requests
CREATE TABLE IF NOT EXISTS store.order_return_requests (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    order_id uuid NOT NULL REFERENCES store.sales_orders(id),
    customer_id uuid NOT NULL REFERENCES store.user_accounts(id),
    return_status varchar(32) NOT NULL DEFAULT 'REQUESTED'
        CHECK (return_status IN ('REQUESTED', 'APPROVED', 'REJECTED', 'CANCELLED', 'RECEIVED', 'REFUNDED')),
    reason_code varchar(32) NOT NULL
        CHECK (reason_code IN ('WRONG_ITEM', 'WRONG_SIZE', 'DEFECTIVE', 'NOT_AS_DESCRIBED', 'OTHER')),
    customer_note text NULL,
    requested_at timestamptz NOT NULL DEFAULT now(),
    decided_by uuid NULL REFERENCES store.user_accounts(id),
    decided_at timestamptz NULL,
    decision_note text NULL,
    received_by uuid NULL REFERENCES store.user_accounts(id),
    received_at timestamptz NULL,
    received_evidence text NULL,
    refund_amount numeric(18,2) NOT NULL,
    revision integer NOT NULL DEFAULT 0 CHECK (revision >= 0),
    CONSTRAINT ck_return_requests_reason CHECK (
        (reason_code <> 'OTHER') OR (reason_code = 'OTHER' AND customer_note IS NOT NULL AND btrim(customer_note) <> '')
    ),
    CONSTRAINT ck_return_requests_customer_note_len CHECK (
        customer_note IS NULL OR length(customer_note) <= 1000
    ),
    CONSTRAINT ck_return_requests_decision_note_len CHECK (
        decision_note IS NULL OR length(decision_note) <= 1000
    ),
    CONSTRAINT ck_return_requests_received_evidence_len CHECK (
        received_evidence IS NULL OR length(received_evidence) <= 500
    ),
    CONSTRAINT ck_return_requests_refund_amount CHECK (
        refund_amount > 0 AND refund_amount = floor(refund_amount)
    ),
    CONSTRAINT ck_return_requests_lifecycle CHECK (
        (return_status = 'REQUESTED' AND decided_by IS NULL AND decided_at IS NULL AND decision_note IS NULL AND received_by IS NULL AND received_at IS NULL AND received_evidence IS NULL)
        OR
        (return_status = 'CANCELLED' AND decided_by IS NULL AND decided_at IS NULL AND received_by IS NULL AND received_at IS NULL AND received_evidence IS NULL)
        OR
        (return_status = 'REJECTED' AND decided_by IS NOT NULL AND decided_at IS NOT NULL AND decided_at >= requested_at AND decision_note IS NOT NULL AND btrim(decision_note) <> '' AND received_by IS NULL AND received_at IS NULL AND received_evidence IS NULL)
        OR
        (return_status = 'APPROVED' AND decided_by IS NOT NULL AND decided_at IS NOT NULL AND decided_at >= requested_at AND received_by IS NULL AND received_at IS NULL AND received_evidence IS NULL)
        OR
        (return_status = 'RECEIVED' AND decided_by IS NOT NULL AND decided_at IS NOT NULL AND received_by IS NOT NULL AND received_at IS NOT NULL AND received_at >= decided_at AND received_evidence IS NOT NULL AND btrim(received_evidence) <> '')
        OR
        (return_status = 'REFUNDED' AND decided_by IS NOT NULL AND decided_at IS NOT NULL AND received_by IS NOT NULL AND received_at IS NOT NULL AND received_evidence IS NOT NULL AND btrim(received_evidence) <> '')
    )
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_order_return_requests_order_id
    ON store.order_return_requests(order_id);

CREATE INDEX IF NOT EXISTS ix_order_return_requests_queue
    ON store.order_return_requests(return_status, requested_at, id);

CREATE INDEX IF NOT EXISTS ix_order_return_requests_customer
    ON store.order_return_requests(customer_id, requested_at DESC);

-- 2. Table: store.order_return_request_lines
CREATE TABLE IF NOT EXISTS store.order_return_request_lines (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    return_request_id uuid NOT NULL REFERENCES store.order_return_requests(id) ON DELETE CASCADE,
    order_item_id uuid NOT NULL REFERENCES store.sales_order_items(id),
    product_sku_id uuid NOT NULL REFERENCES store.product_skus(id),
    requested_qty integer NOT NULL CHECK (requested_qty > 0),
    product_title_snapshot varchar(255) NOT NULL,
    sku_code_snapshot varchar(64) NOT NULL,
    option_label_snapshot varchar(100) NOT NULL,
    unit_price numeric(18,2) NOT NULL CHECK (unit_price >= 0 AND unit_price = floor(unit_price)),
    gross_amount numeric(18,2) NOT NULL CHECK (gross_amount >= 0 AND gross_amount = floor(gross_amount)),
    allocated_voucher_discount numeric(18,2) NOT NULL DEFAULT 0 CHECK (allocated_voucher_discount >= 0 AND allocated_voucher_discount = floor(allocated_voucher_discount)),
    refund_amount numeric(18,2) NOT NULL CHECK (refund_amount > 0 AND refund_amount <= gross_amount AND refund_amount = floor(refund_amount)),
    CONSTRAINT ck_return_lines_gross CHECK (gross_amount = requested_qty * unit_price),
    CONSTRAINT ck_return_lines_discount CHECK (allocated_voucher_discount <= gross_amount),
    CONSTRAINT ux_order_return_request_lines UNIQUE (return_request_id, order_item_id)
);

CREATE INDEX IF NOT EXISTS ix_order_return_request_lines_return
    ON store.order_return_request_lines(return_request_id);

CREATE INDEX IF NOT EXISTS ix_order_return_request_lines_sku
    ON store.order_return_request_lines(product_sku_id);

CREATE INDEX IF NOT EXISTS ix_order_return_request_lines_order_item
    ON store.order_return_request_lines(order_item_id);

-- 3. Extend store.payments constraint with PARTIALLY_REFUNDED
DO $$
BEGIN
    ALTER TABLE store.payments DROP CONSTRAINT IF EXISTS payments_payment_status_check;
    ALTER TABLE store.payments DROP CONSTRAINT IF EXISTS ck_payments_status;
    ALTER TABLE store.payments
        ADD CONSTRAINT ck_payments_status
        CHECK (payment_status IN ('PENDING','FAILED','SUCCESS','VOID','LATE_PAID','REFUND_REQUIRED','REFUNDED','PARTIALLY_REFUNDED','COD_PENDING','COD_COLLECTED'));
END $$;

-- 4. Extend store.manual_refunds with return_request_id and unique evidence reference
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'store' AND table_name = 'manual_refunds' AND column_name = 'return_request_id'
    ) THEN
        ALTER TABLE store.manual_refunds
            ADD COLUMN return_request_id uuid NULL REFERENCES store.order_return_requests(id);
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_manual_refunds_return_request_id
    ON store.manual_refunds(return_request_id)
    WHERE return_request_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_manual_refunds_upper_evidence_ref
    ON store.manual_refunds(upper(btrim(evidence_reference)));

COMMIT;

