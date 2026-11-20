-- T50.8 deterministic benchmark dataset. NEVER mount or run this as a migration/demo seed.
-- Intended only for a disposable database whose name starts with hcc_perf_.
DO $$
BEGIN
    IF current_database() !~ '^hcc_perf_[0-9a-f]{32}$' THEN
        RAISE EXCEPTION 'Refusing benchmark seed on non-disposable database: %', current_database();
    END IF;
END $$;

INSERT INTO store.roles(id, role_key, display_name)
VALUES
    (md5('hcc-benchmark-admin-role')::uuid, 'ADMIN', 'Benchmark Admin'),
    (md5('hcc-benchmark-customer-role')::uuid, 'CUSTOMER', 'Benchmark Customer')
ON CONFLICT (role_key) DO NOTHING;

INSERT INTO store.user_accounts(id, role_id, email, password_hash, full_name, account_status, email_verified_at)
VALUES
    (md5('hcc-benchmark-admin')::uuid, (SELECT id FROM store.roles WHERE role_key = 'ADMIN'),
        'admin@benchmark.invalid', 'benchmark-login-disabled', 'Benchmark Admin', 'ACTIVE', now()),
    (md5('hcc-benchmark-customer')::uuid, (SELECT id FROM store.roles WHERE role_key = 'CUSTOMER'),
        'customer@benchmark.invalid', 'benchmark-login-disabled', 'Benchmark Customer', 'ACTIVE', now());

INSERT INTO store.categories(id, name, slug, is_visible)
SELECT md5('hcc-benchmark-category-' || n)::uuid,
       'Danh mục cầu lông ' || lpad(n::text, 2, '0'),
       'benchmark-category-' || lpad(n::text, 2, '0'),
       true
FROM generate_series(1, 20) AS series(n);

INSERT INTO store.brands(id, name, slug, country, is_active)
SELECT md5('hcc-benchmark-brand-' || n)::uuid,
       'Benchmark Brand ' || lpad(n::text, 2, '0'),
       'benchmark-brand-' || lpad(n::text, 2, '0'),
       CASE WHEN n % 2 = 0 THEN 'VN' ELSE 'JP' END,
       true
FROM generate_series(1, 20) AS series(n);

INSERT INTO store.products(
    id, created_at, updated_at, category_id, brand_id, title, description,
    publication_status, created_by, published_by, published_at)
SELECT md5('hcc-benchmark-product-' || n)::uuid,
       timestamptz '2026-01-01 00:00:00+00' + (n || ' seconds')::interval,
       timestamptz '2026-01-01 00:00:00+00' + (n || ' seconds')::interval,
       md5('hcc-benchmark-category-' || (((n - 1) % 20) + 1))::uuid,
       md5('hcc-benchmark-brand-' || (((n - 1) % 20) + 1))::uuid,
       'Vợt ' || substr(md5(n::text), 1, 8) || ' '
           || substr(md5('model-' || n), 1, 8) || ' '
           || substr(md5('variant-' || n), 1, 8) || ' '
           || substr(md5('series-' || n), 1, 8) || ' Benchmark ' || lpad(n::text, 5, '0'),
       repeat('Mô tả benchmark thông số kỹ thuật, vật liệu, độ cứng và đối tượng sử dụng. ', 24)
           || 'Sản phẩm số ' || n,
       'PUBLISHED',
       md5('hcc-benchmark-admin')::uuid,
       md5('hcc-benchmark-admin')::uuid,
       timestamptz '2026-01-01 00:00:00+00' + (n || ' seconds')::interval
FROM generate_series(1, 10000) AS series(n);

INSERT INTO store.product_skus(
    id, created_at, updated_at, product_id, sku_code, option_label, regular_price, sku_status)
SELECT md5('hcc-benchmark-sku-' || product_no || '-' || sku_no)::uuid,
       timestamptz '2026-01-01 00:00:00+00',
       timestamptz '2026-01-01 00:00:00+00',
       md5('hcc-benchmark-product-' || product_no)::uuid,
       'BENCH-' || lpad(product_no::text, 5, '0') || '-' || sku_no,
       CASE sku_no WHEN 1 THEN '4U G5 Đỏ' WHEN 2 THEN '3U G5 Xanh' ELSE '4U G6 Trắng' END,
       500000 + ((product_no % 60) * 50000) + (sku_no * 10000),
       'ACTIVE'
FROM generate_series(1, 10000) AS products(product_no)
CROSS JOIN generate_series(1, 3) AS skus(sku_no);

INSERT INTO store.inventory_balances(
    id, product_sku_id, physical_qty, available_qty, reserved_qty, allocated_qty, revision)
SELECT md5('hcc-benchmark-balance-' || product_no || '-' || sku_no)::uuid,
       md5('hcc-benchmark-sku-' || product_no || '-' || sku_no)::uuid,
       CASE WHEN (product_no + sku_no) % 4 = 0 THEN 0 ELSE 25 END,
       CASE WHEN (product_no + sku_no) % 4 = 0 THEN 0 ELSE 25 END,
       0, 0, 0
FROM generate_series(1, 10000) AS products(product_no)
CROSS JOIN generate_series(1, 3) AS skus(sku_no);

-- 2,500 completed/delivered orders produce representative 90-day popularity
-- and rating aggregates without bloating the normal demo seed.
INSERT INTO store.sales_orders(
    id, created_at, updated_at, customer_id, order_number, payment_method, order_status,
    shipping_address_snapshot, total_amount, subtotal_amount, voucher_discount_amount,
    deadline_at, confirmed_at)
SELECT md5('hcc-benchmark-order-' || n)::uuid,
       now() - (((n - 1) % 89) || ' days')::interval,
       now() - (((n - 1) % 89) || ' days')::interval,
       md5('hcc-benchmark-customer')::uuid,
       'BENCH-' || lpad(n::text, 7, '0'),
       'COD', 'COMPLETED', '{"source":"T50.8 benchmark"}'::jsonb,
       510000 + ((n % 60) * 50000),
       510000 + ((n % 60) * 50000),
       0,
       now() + interval '1 day',
       now() - (((n - 1) % 89) || ' days')::interval
FROM generate_series(1, 2500) AS series(n);

INSERT INTO store.sales_order_items(
    id, created_at, updated_at, order_id, product_sku_id, product_title_snapshot,
    sku_code_snapshot, sku_option_label_snapshot, quantity, unit_price)
SELECT md5('hcc-benchmark-order-item-' || n)::uuid,
       now() - (((n - 1) % 89) || ' days')::interval,
       now() - (((n - 1) % 89) || ' days')::interval,
       md5('hcc-benchmark-order-' || n)::uuid,
       md5('hcc-benchmark-sku-' || n || '-1')::uuid,
       'Vợt ' || substr(md5(n::text), 1, 8) || ' '
           || substr(md5('model-' || n), 1, 8) || ' '
           || substr(md5('variant-' || n), 1, 8) || ' '
           || substr(md5('series-' || n), 1, 8) || ' Benchmark ' || lpad(n::text, 5, '0'),
       'BENCH-' || lpad(n::text, 5, '0') || '-1',
       '4U G5 Đỏ', 1,
       510000 + ((n % 60) * 50000)
FROM generate_series(1, 2500) AS series(n);

INSERT INTO store.shipments(id, created_at, updated_at, order_id, shipment_status, delivered_at)
SELECT md5('hcc-benchmark-shipment-' || n)::uuid,
       now() - (((n - 1) % 89) || ' days')::interval,
       now() - (((n - 1) % 89) || ' days')::interval,
       md5('hcc-benchmark-order-' || n)::uuid,
       'DELIVERED',
       now() - (((n - 1) % 89) || ' days')::interval
FROM generate_series(1, 2500) AS series(n);

INSERT INTO store.product_reviews(
    id, created_at, updated_at, customer_id, product_id, qualifying_order_item_id,
    rating, content, review_status)
SELECT md5('hcc-benchmark-review-' || n)::uuid,
       now() - (((n - 1) % 89) || ' days')::interval,
       now() - (((n - 1) % 89) || ' days')::interval,
       md5('hcc-benchmark-customer')::uuid,
       md5('hcc-benchmark-product-' || n)::uuid,
       md5('hcc-benchmark-order-item-' || n)::uuid,
       ((n - 1) % 5) + 1,
       'Đánh giá benchmark đủ độ dài cho sản phẩm số ' || n,
       'PUBLISHED'
FROM generate_series(1, 2500) AS series(n);

ANALYZE store.categories;
ANALYZE store.brands;
ANALYZE store.products;
ANALYZE store.product_skus;
ANALYZE store.inventory_balances;
ANALYZE store.sales_orders;
ANALYZE store.sales_order_items;
ANALYZE store.shipments;
ANALYZE store.product_reviews;
