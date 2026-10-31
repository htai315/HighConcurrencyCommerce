-- High-Concurrency E-commerce | SDD Part 4 | PostgreSQL | ALIGNED DESIGN DRAFT v1.0.1
-- Target: PostgreSQL 16+; schema is a design proposal requiring DB integration testing.
-- 31 business entities E01-E31; optional infrastructure Outbox is a separate file.
-- Alignment 2026-10-30: Order COD PENDING_COD_CONFIRMATION; Payment COD_PENDING/COD_COLLECTED/FAILED;
-- additional snapshot/image status and one-pending-cancellation index (proposed physical design).
-- IMPORTANT: COD cancellation payment terminal semantics, PayOS integration and application cross-table guards
-- are not yet approved/verified; this SQL is NOT production ready.
BEGIN;
CREATE SCHEMA IF NOT EXISTS store;
SET search_path TO store, public;

-- E02 Role
CREATE TABLE store.roles (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    role_key text NOT NULL UNIQUE CHECK (role_key IN ('CUSTOMER','STAFF','ADMIN')),
    display_name text NOT NULL
);

-- E01 UserAccount
CREATE TABLE store.user_accounts (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    role_id uuid NOT NULL REFERENCES store.roles(id),
    email varchar(320) NOT NULL,
    password_hash text NOT NULL,
    full_name varchar(160) NOT NULL,
    phone varchar(30),
    account_status text NOT NULL DEFAULT 'ACTIVE' CHECK (account_status IN ('ACTIVE','LOCKED')),
    locked_at timestamptz
);

-- E03 CustomerAddress
CREATE TABLE store.customer_addresses (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    customer_id uuid NOT NULL REFERENCES store.user_accounts(id),
    recipient_name varchar(160) NOT NULL,
    phone varchar(30) NOT NULL,
    address_line text NOT NULL,
    ward varchar(160),
    province varchar(160) NOT NULL,
    is_default boolean NOT NULL DEFAULT false
);

-- E04 Category
CREATE TABLE store.categories (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    name varchar(180) NOT NULL,
    slug varchar(220) NOT NULL UNIQUE,
    is_visible boolean NOT NULL DEFAULT true
);

-- E05 Product
CREATE TABLE store.products (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    category_id uuid REFERENCES store.categories(id),
    title varchar(240) NOT NULL,
    description text,
    publication_status text NOT NULL DEFAULT 'DRAFT' CHECK (publication_status IN ('DRAFT','PUBLISHED','ARCHIVED')),
    created_by uuid NOT NULL REFERENCES store.user_accounts(id),
    published_by uuid REFERENCES store.user_accounts(id),
    published_at timestamptz
);

-- E06 ProductSku
CREATE TABLE store.product_skus (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    product_id uuid NOT NULL REFERENCES store.products(id),
    sku_code varchar(100) NOT NULL UNIQUE,
    option_label varchar(200) NOT NULL,
    regular_price numeric(18,2) NOT NULL CHECK (regular_price >= 0),
    sku_status text NOT NULL DEFAULT 'ACTIVE' CHECK (sku_status IN ('ACTIVE','INACTIVE'))
);

-- E07 ProductImage
CREATE TABLE store.product_images (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    product_id uuid NOT NULL REFERENCES store.products(id),
    file_key text NOT NULL,
    image_status text NOT NULL DEFAULT 'ACTIVE' CHECK (image_status IN ('ACTIVE','INACTIVE')),
    alt_text varchar(240),
    display_order integer NOT NULL DEFAULT 0 CHECK (display_order >= 0)
);

-- E08 InventoryBalance
CREATE TABLE store.inventory_balances (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    product_sku_id uuid NOT NULL UNIQUE REFERENCES store.product_skus(id),
    physical_qty integer NOT NULL DEFAULT 0,
    available_qty integer NOT NULL DEFAULT 0,
    reserved_qty integer NOT NULL DEFAULT 0,
    allocated_qty integer NOT NULL DEFAULT 0,
    revision bigint NOT NULL DEFAULT 0,
    CONSTRAINT ck_inventory_nonnegative CHECK (physical_qty >= 0 AND available_qty >= 0 AND reserved_qty >= 0 AND allocated_qty >= 0),
    CONSTRAINT ck_inventory_partition CHECK (physical_qty = available_qty + reserved_qty + allocated_qty)
);

-- E09 InventoryMovement
CREATE TABLE store.inventory_movements (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    product_sku_id uuid NOT NULL REFERENCES store.product_skus(id),
    movement_type text NOT NULL CHECK (movement_type IN ('STOCK_IN','RESERVE','RELEASE','ALLOCATE','STOCK_OUT','RETURN_IN','ADJUST')),
    qty_delta integer NOT NULL CHECK (qty_delta <> 0),
    source_type varchar(60) NOT NULL,
    source_id uuid NOT NULL,
    actor_id uuid REFERENCES store.user_accounts(id),
    note text,
    UNIQUE (product_sku_id,
    movement_type,
    source_type,
    source_id)
);

-- E10 InventoryAdjustmentRequest
CREATE TABLE store.inventory_adjustment_requests (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    product_sku_id uuid NOT NULL REFERENCES store.product_skus(id),
    requested_by uuid NOT NULL REFERENCES store.user_accounts(id),
    reviewed_by uuid REFERENCES store.user_accounts(id),
    delta_qty integer NOT NULL CHECK (delta_qty < 0),
    reason text NOT NULL,
    review_status text NOT NULL DEFAULT 'PENDING' CHECK (review_status IN ('PENDING','APPROVED','REJECTED','APPLIED')),
    reviewed_at timestamptz,
    CHECK (reviewed_by IS NULL OR reviewed_by <> requested_by)
);

-- E13 ShoppingCart
CREATE TABLE store.shopping_carts (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    customer_id uuid NOT NULL UNIQUE REFERENCES store.user_accounts(id),
    cart_status text NOT NULL DEFAULT 'ACTIVE' CHECK (cart_status IN ('ACTIVE','CLOSED'))
);

-- E14 CartItem
CREATE TABLE store.cart_items (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    cart_id uuid NOT NULL REFERENCES store.shopping_carts(id),
    product_sku_id uuid NOT NULL REFERENCES store.product_skus(id),
    quantity integer NOT NULL CHECK (quantity > 0),
    UNIQUE (cart_id,
    product_sku_id)
);

-- E15 SalesOrder
CREATE TABLE store.sales_orders (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    customer_id uuid NOT NULL REFERENCES store.user_accounts(id),
    shipping_address_id uuid REFERENCES store.customer_addresses(id),
    order_number varchar(40) NOT NULL UNIQUE,
    payment_method text NOT NULL CHECK (payment_method IN ('PAYOS','COD')),
    order_status text NOT NULL CHECK (order_status IN ('PENDING_PAYMENT','PENDING_COD_CONFIRMATION','CONFIRMED','FULFILLING','COMPLETED','CANCELLED','EXPIRED')),
    shipping_address_snapshot jsonb NOT NULL CHECK (jsonb_typeof(shipping_address_snapshot) = 'object'),
    total_amount numeric(18,2) NOT NULL CHECK (total_amount >= 0),
    deadline_at timestamptz NOT NULL,
    closed_reason varchar(100),
    confirmed_at timestamptz,
    CHECK (deadline_at > created_at)
);

-- E16 SalesOrderItem
CREATE TABLE store.sales_order_items (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    order_id uuid NOT NULL REFERENCES store.sales_orders(id),
    product_sku_id uuid NOT NULL REFERENCES store.product_skus(id),
    product_title_snapshot varchar(240) NOT NULL,
    sku_code_snapshot varchar(100) NOT NULL,
    sku_option_label_snapshot varchar(200) NOT NULL,
    quantity integer NOT NULL CHECK (quantity > 0),
    unit_price numeric(18,2) NOT NULL CHECK (unit_price >= 0),
    line_total numeric(18,2) GENERATED ALWAYS AS (quantity * unit_price) STORED,
    UNIQUE (order_id,
    product_sku_id)
);

-- E17 CheckoutRequest
CREATE TABLE store.checkout_requests (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    customer_id uuid NOT NULL REFERENCES store.user_accounts(id),
    idempotency_key varchar(160) NOT NULL,
    payload_hash char(64) NOT NULL,
    request_status text NOT NULL DEFAULT 'PROCESSING' CHECK (request_status IN ('PROCESSING','SUCCEEDED','REJECTED')),
    order_id uuid UNIQUE REFERENCES store.sales_orders(id),
    response_snapshot jsonb,
    UNIQUE (customer_id,
    idempotency_key)
);

-- E18 OrderCancellationRequest
CREATE TABLE store.order_cancellation_requests (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    order_id uuid NOT NULL REFERENCES store.sales_orders(id),
    requested_by uuid NOT NULL REFERENCES store.user_accounts(id),
    decided_by uuid REFERENCES store.user_accounts(id),
    reason text NOT NULL,
    decision text NOT NULL DEFAULT 'PENDING' CHECK (decision IN ('PENDING','APPROVED','REJECTED')),
    decided_at timestamptz
);

-- E11 StockReservation
CREATE TABLE store.stock_reservations (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    order_id uuid NOT NULL UNIQUE REFERENCES store.sales_orders(id),
    reservation_status text NOT NULL DEFAULT 'HELD' CHECK (reservation_status IN ('HELD','ALLOCATED','RELEASED','CONSUMED')),
    reserved_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    CHECK (expires_at > reserved_at)
);

-- E12 StockReservationLine
CREATE TABLE store.stock_reservation_lines (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    reservation_id uuid NOT NULL REFERENCES store.stock_reservations(id),
    order_item_id uuid NOT NULL UNIQUE REFERENCES store.sales_order_items(id),
    product_sku_id uuid NOT NULL REFERENCES store.product_skus(id),
    quantity integer NOT NULL CHECK (quantity > 0),
    line_status text NOT NULL DEFAULT 'HELD' CHECK (line_status IN ('HELD','ALLOCATED','RELEASED','CONSUMED')),
    UNIQUE (reservation_id,
    product_sku_id)
);

-- E19 Payment
CREATE TABLE store.payments (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    order_id uuid NOT NULL UNIQUE REFERENCES store.sales_orders(id),
    payment_method text NOT NULL CHECK (payment_method IN ('PAYOS','COD')),
    expected_amount numeric(18,2) NOT NULL CHECK (expected_amount >= 0),
    -- IMPORTANT: application must supply COD_PENDING for COD orders (default PENDING is for PayOS).
    payment_status text NOT NULL DEFAULT 'PENDING' CHECK (payment_status IN ('PENDING','FAILED','SUCCESS','VOID','LATE_PAID','REFUND_REQUIRED','REFUNDED','COD_PENDING','COD_COLLECTED')),
    paid_at timestamptz
);

-- E20 PaymentAttempt
CREATE TABLE store.payment_attempts (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    payment_id uuid NOT NULL REFERENCES store.payments(id),
    attempt_no smallint NOT NULL CHECK (attempt_no BETWEEN 1 AND 3),
    provider_order_code bigint UNIQUE,
    provider_link text,
    expires_at timestamptz NOT NULL,
    attempt_status text NOT NULL DEFAULT 'CREATED' CHECK (attempt_status IN ('CREATED','LINK_READY','FAILED','SUCCESS','EXPIRED')),
    UNIQUE (payment_id,
    attempt_no)
);

-- E21 PaymentTransaction
CREATE TABLE store.payment_transactions (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    payment_id uuid NOT NULL REFERENCES store.payments(id),
    attempt_id uuid REFERENCES store.payment_attempts(id),
    provider text NOT NULL CHECK (provider IN ('PAYOS','COD')),
    provider_transaction_id varchar(180),
    amount numeric(18,2) NOT NULL CHECK (amount > 0),
    provider_paid_at timestamptz,
    verified_at timestamptz NOT NULL DEFAULT now(),
    transaction_status text NOT NULL CHECK (transaction_status IN ('VERIFIED','RECONCILIATION_REQUIRED')),
    CHECK (provider = 'COD' OR provider_transaction_id IS NOT NULL)
);

-- E22 ProviderCallbackEvent
CREATE TABLE store.provider_callback_events (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    attempt_id uuid REFERENCES store.payment_attempts(id),
    provider_order_code bigint,
    payload_hash char(64) NOT NULL,
    payload jsonb NOT NULL CHECK (jsonb_typeof(payload) = 'object'),
    signature_valid boolean NOT NULL,
    received_at timestamptz NOT NULL DEFAULT now(),
    process_result text NOT NULL CHECK (process_result IN ('APPLIED','DUPLICATE','REJECTED','LATE_PAID','ERROR'))
);

-- E23 PaymentReconciliationCase
CREATE TABLE store.payment_reconciliation_cases (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    payment_id uuid NOT NULL REFERENCES store.payments(id),
    reason text NOT NULL CHECK (reason IN ('LATE_PAID','DUPLICATE_COLLECTION','AMOUNT_MISMATCH','OTHER')),
    case_status text NOT NULL DEFAULT 'OPEN' CHECK (case_status IN ('OPEN','IN_REVIEW','RESOLVED')),
    resolved_by uuid REFERENCES store.user_accounts(id),
    resolved_at timestamptz,
    note text
);

-- E24 ManualRefund
CREATE TABLE store.manual_refunds (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    transaction_id uuid NOT NULL UNIQUE REFERENCES store.payment_transactions(id),
    admin_id uuid NOT NULL REFERENCES store.user_accounts(id),
    amount numeric(18,2) NOT NULL CHECK (amount > 0),
    evidence_reference text NOT NULL,
    refund_status text NOT NULL DEFAULT 'REFUND_REQUIRED' CHECK (refund_status IN ('REFUND_REQUIRED','REFUNDED')),
    refunded_at timestamptz
);

-- E25 FlashSaleCampaign
CREATE TABLE store.flash_sale_campaigns (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    name varchar(240) NOT NULL,
    starts_at timestamptz NOT NULL,
    ends_at timestamptz NOT NULL,
    campaign_status text NOT NULL DEFAULT 'DRAFT' CHECK (campaign_status IN ('DRAFT','SCHEDULED','ACTIVE','ENDED','CANCELLED')),
    created_by uuid NOT NULL REFERENCES store.user_accounts(id),
    CHECK (ends_at > starts_at)
);

-- E26 FlashSaleOffer
CREATE TABLE store.flash_sale_offers (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    campaign_id uuid NOT NULL REFERENCES store.flash_sale_campaigns(id),
    product_sku_id uuid NOT NULL REFERENCES store.product_skus(id),
    sale_price numeric(18,2) NOT NULL CHECK (sale_price >= 0),
    quota_total integer NOT NULL CHECK (quota_total >= 0),
    quota_held integer NOT NULL DEFAULT 0,
    quota_sold integer NOT NULL DEFAULT 0,
    per_customer_limit smallint NOT NULL DEFAULT 2 CHECK (per_customer_limit > 0),
    CONSTRAINT ck_flash_sale_quota CHECK (quota_held >= 0 AND quota_sold >= 0 AND quota_held + quota_sold <= quota_total),
    UNIQUE (campaign_id,
    product_sku_id)
);

-- E27 FlashSaleAllocation
CREATE TABLE store.flash_sale_allocations (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    offer_id uuid NOT NULL REFERENCES store.flash_sale_offers(id),
    order_item_id uuid NOT NULL UNIQUE REFERENCES store.sales_order_items(id),
    quantity integer NOT NULL CHECK (quantity > 0),
    allocation_status text NOT NULL CHECK (allocation_status IN ('HELD','SOLD','RELEASED')),
    released_at timestamptz
);

-- E28 Shipment
CREATE TABLE store.shipments (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    order_id uuid NOT NULL UNIQUE REFERENCES store.sales_orders(id),
    shipment_status text NOT NULL DEFAULT 'PENDING_PACKING' CHECK (shipment_status IN ('PENDING_PACKING','PACKED','SHIPPING','DELIVERED','DELIVERY_FAILED')),
    redelivery_count smallint NOT NULL DEFAULT 0 CHECK (redelivery_count BETWEEN 0 AND 2),
    return_stock_completed boolean NOT NULL DEFAULT false,
    shipped_at timestamptz,
    delivered_at timestamptz
);

-- E29 DeliveryAttempt
CREATE TABLE store.delivery_attempts (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    shipment_id uuid NOT NULL REFERENCES store.shipments(id),
    attempt_no smallint NOT NULL CHECK (attempt_no BETWEEN 1 AND 3),
    result text NOT NULL CHECK (result IN ('IN_PROGRESS','DELIVERED','FAILED')),
    note text,
    finished_at timestamptz,
    UNIQUE (shipment_id,
    attempt_no)
);

-- E30 InAppNotification
CREATE TABLE store.in_app_notifications (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    recipient_id uuid NOT NULL REFERENCES store.user_accounts(id),
    event_key varchar(180) NOT NULL,
    notification_type varchar(80) NOT NULL,
    title varchar(200) NOT NULL,
    body text NOT NULL,
    read_at timestamptz,
    UNIQUE (recipient_id,
    event_key)
);

-- E31 AuditLog
CREATE TABLE store.audit_logs (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    actor_id uuid REFERENCES store.user_accounts(id),
    actor_type varchar(30) NOT NULL CHECK (actor_type IN ('USER','WORKER','SYSTEM')),
    action varchar(100) NOT NULL,
    entity_type varchar(100) NOT NULL,
    entity_id uuid NOT NULL,
    before_data jsonb,
    after_data jsonb,
    correlation_id varchar(100),
    occurred_at timestamptz NOT NULL DEFAULT now()
);
-- Application must still enforce cross-row and cross-table rules in transactions.
CREATE UNIQUE INDEX ux_user_accounts_email_lower ON store.user_accounts (lower(email));
CREATE UNIQUE INDEX ux_default_address_per_customer ON store.customer_addresses (customer_id) WHERE is_default;
CREATE INDEX ix_product_skus_product ON store.product_skus(product_id);
CREATE INDEX ix_products_public ON store.products(publication_status, created_at DESC);
CREATE INDEX ix_inventory_movements_sku_time ON store.inventory_movements(product_sku_id, created_at DESC);
CREATE INDEX ix_adjustments_pending ON store.inventory_adjustment_requests(review_status, created_at) WHERE review_status = 'PENDING';
-- Guard one unresolved cancellation request per order; approved decisions require an explicit review.
CREATE UNIQUE INDEX ux_one_pending_cancellation_per_order ON store.order_cancellation_requests(order_id) WHERE decision = 'PENDING';
CREATE INDEX ix_order_customer_time ON store.sales_orders(customer_id, created_at DESC);
CREATE INDEX ix_order_expiry_pending ON store.sales_orders(deadline_at) WHERE order_status IN ('PENDING_PAYMENT','PENDING_COD_CONFIRMATION');
CREATE INDEX ix_cod_pending_customer ON store.sales_orders(customer_id) WHERE payment_method = 'COD' AND order_status = 'PENDING_COD_CONFIRMATION';
CREATE INDEX ix_reservations_expiry ON store.stock_reservations(expires_at) WHERE reservation_status = 'HELD';
CREATE INDEX ix_srl_reservation ON store.stock_reservation_lines(reservation_id);
CREATE INDEX ix_attempts_payment ON store.payment_attempts(payment_id, attempt_no);
CREATE UNIQUE INDEX ux_payos_transaction_external ON store.payment_transactions(provider,provider_transaction_id) WHERE provider_transaction_id IS NOT NULL;
CREATE INDEX ix_payment_transactions_payment ON store.payment_transactions(payment_id, verified_at DESC);
CREATE INDEX ix_callbacks_order_received ON store.provider_callback_events(provider_order_code, received_at DESC);
CREATE INDEX ix_reconciliation_open ON store.payment_reconciliation_cases(case_status, created_at) WHERE case_status <> 'RESOLVED';
CREATE INDEX ix_flash_sale_active ON store.flash_sale_campaigns(starts_at, ends_at) WHERE campaign_status IN ('SCHEDULED','ACTIVE');
CREATE INDEX ix_flash_sale_alloc_offer_state ON store.flash_sale_allocations(offer_id,allocation_status);
CREATE INDEX ix_delivery_attempt_shipment ON store.delivery_attempts(shipment_id,attempt_no);
CREATE INDEX ix_notifications_unread ON store.in_app_notifications(recipient_id, created_at DESC) WHERE read_at IS NULL;
CREATE INDEX ix_audit_entity_time ON store.audit_logs(entity_type, entity_id, occurred_at DESC);
COMMIT;
