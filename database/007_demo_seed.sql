-- =============================================================================
-- CẢNH BÁO BẢO MẬT: FILE NÀY CHỈ DÙNG CHO MÔI TRƯỜNG LOCAL / DEMO / E2E TESTING.
-- TUYỆT ĐỐI KHÔNG CHẠY TRÊN MÔI TRƯỜNG PRODUCTION HOẶC HỆ THỐNG MỞ CÔNG KHAI!
-- Mật khẩu 3 tài khoản demo đã được công khai: DemoPass123!@#
-- =============================================================================
-- 007_demo_seed.sql
-- Seed dữ liệu demo thương mại và 3 tài khoản role ổn định
-- Mật khẩu chuẩn cho cả 3 tài khoản demo: DemoPass123!@#
-- Script an toàn, có thể chạy lại nhiều lần (idempotent qua ON CONFLICT DO NOTHING)

BEGIN;

-- 1. Đảm bảo 3 vai trò chuẩn tồn tại
INSERT INTO store.roles (id, role_key, display_name) VALUES
    ('00000000-0000-0000-0000-000000000001', 'ADMIN', 'Quản trị viên'),
    ('00000000-0000-0000-0000-000000000002', 'STAFF', 'Nhân viên vận hành'),
    ('00000000-0000-0000-0000-000000000003', 'CUSTOMER', 'Khách hàng')
ON CONFLICT (role_key) DO UPDATE SET display_name = EXCLUDED.display_name;

-- 2. Ba tài khoản role demo (PasswordHash được sinh bởi ASP.NET Core Identity PasswordHasher, 210.000 rounds PBKDF2)
-- Password: DemoPass123!@#
INSERT INTO store.user_accounts (id, role_id, email, password_hash, full_name, phone, account_status, email_verified_at, created_at, updated_at) VALUES
    ('a0000000-0000-0000-0000-000000000001',
     (SELECT id FROM store.roles WHERE role_key = 'ADMIN'),
     'admin@badminton.local',
     'AQAAAAIAAzRQAAAAEIXLDyMdn4IoUrJbnr7BzizFVJapLJn8DYNgGZ0GiBgQkJARQG2n7bun8wUbgHiEpg==',
     'Hệ Thống Admin',
     '0909000001',
     'ACTIVE',
     now(),
     now(),
     now()),
    ('a0000000-0000-0000-0000-000000000002',
     (SELECT id FROM store.roles WHERE role_key = 'STAFF'),
     'staff@badminton.local',
     'AQAAAAIAAzRQAAAAEIXLDyMdn4IoUrJbnr7BzizFVJapLJn8DYNgGZ0GiBgQkJARQG2n7bun8wUbgHiEpg==',
     'Nguyễn Văn Staff',
     '0909000002',
     'ACTIVE',
     now(),
     now(),
     now()),
    ('a0000000-0000-0000-0000-000000000003',
     (SELECT id FROM store.roles WHERE role_key = 'CUSTOMER'),
     'customer@badminton.local',
     'AQAAAAIAAzRQAAAAEIXLDyMdn4IoUrJbnr7BzizFVJapLJn8DYNgGZ0GiBgQkJARQG2n7bun8wUbgHiEpg==',
     'Trần Thị Khách',
     '0909000003',
     'ACTIVE',
     now(),
     now(),
     now())
ON CONFLICT (id) DO NOTHING;

-- 3. Địa chỉ giao hàng mặc định cho tài khoản Customer demo
INSERT INTO store.customer_addresses (id, customer_id, recipient_name, phone, address_line, ward, province, is_default, created_at, updated_at) VALUES
    ('d0000000-0000-0000-0000-000000000001',
     'a0000000-0000-0000-0000-000000000003',
     'Trần Thị Khách',
     '0909000003',
     '123 Đường Thể Thao',
     'Phường Bến Nghé',
     'TP. Hồ Chí Minh',
     true,
     now(),
     now())
ON CONFLICT (id) DO NOTHING;

-- 4. Danh mục sản phẩm (Categories)
INSERT INTO store.categories (id, name, slug, is_visible, created_at, updated_at) VALUES
    ('c0000000-0000-0000-0000-000000000001', 'Vợt cầu lông', 'vot-cau-long', true, now(), now()),
    ('c0000000-0000-0000-0000-000000000002', 'Giày cầu lông', 'giay-cau-long', true, now(), now()),
    ('c0000000-0000-0000-0000-000000000003', 'Phụ kiện & Cước', 'phu-kien-cuoc', true, now(), now())
ON CONFLICT (slug) DO NOTHING;

-- 4.1. Thương hiệu đại diện cho catalog search/facet demo.
INSERT INTO store.brands (id, name, slug, country, website_url, is_active, created_at, updated_at) VALUES
    ('bb000000-0000-0000-0000-000000000001', 'Yonex', 'yonex', 'Nhật Bản', 'https://www.yonex.com', true, now(), now()),
    ('bb000000-0000-0000-0000-000000000002', 'Victor', 'victor', 'Đài Loan', 'https://www.victorsport.com', true, now(), now())
ON CONFLICT (slug) DO UPDATE SET
    name = EXCLUDED.name,
    country = EXCLUDED.country,
    website_url = EXCLUDED.website_url,
    is_active = true,
    updated_at = now();

-- 5. Sản phẩm mẫu (Products)
INSERT INTO store.products (id, title, description, publication_status, category_id, created_by, published_by, published_at, created_at, updated_at) VALUES
    ('b0000000-0000-0000-0000-000000000001',
     'Vợt cầu lông Yonex Astrox 88D Pro',
     'Vợt tấn công đỉnh cao dành cho người chơi phong trào và bán chuyên nghiệp với công nghệ Rotational Generator System.',
     'PUBLISHED',
     (SELECT id FROM store.categories WHERE slug = 'vot-cau-long'),
     'a0000000-0000-0000-0000-000000000001',
     'a0000000-0000-0000-0000-000000000001',
     now(),
     now(),
     now()),
    ('b0000000-0000-0000-0000-000000000002',
     'Vợt cầu lông Victor Thruster Ryuga II',
     'Vợt công thủ toàn diện với độ đầm đầu cao, tăng lực đập cầu tối đa, thân vợt WES 2.0 đàn hồi vượt trội.',
     'PUBLISHED',
     (SELECT id FROM store.categories WHERE slug = 'vot-cau-long'),
     'a0000000-0000-0000-0000-000000000001',
     'a0000000-0000-0000-0000-000000000001',
     now(),
     now(),
     now()),
    ('b0000000-0000-0000-0000-000000000003',
     'Giày cầu lông Yonex Power Cushion 65 Z3',
     'Đế Power Cushion+ giảm chấn tối ưu, bám sân cực tốt và hỗ trợ di chuyển nhanh nhẹn trên sân thi đấu.',
     'PUBLISHED',
     (SELECT id FROM store.categories WHERE slug = 'giay-cau-long'),
     'a0000000-0000-0000-0000-000000000001',
     'a0000000-0000-0000-0000-000000000001',
     now(),
     now(),
     now())
ON CONFLICT (id) DO NOTHING;

-- Gắn brand cả khi script được chạy lại trên database demo đã tồn tại.
UPDATE store.products
SET brand_id = CASE
        WHEN id IN ('b0000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000003')
            THEN 'bb000000-0000-0000-0000-000000000001'::uuid
        ELSE 'bb000000-0000-0000-0000-000000000002'::uuid
    END,
    updated_at = now()
WHERE id IN (
    'b0000000-0000-0000-0000-000000000001',
    'b0000000-0000-0000-0000-000000000002',
    'b0000000-0000-0000-0000-000000000003')
  AND brand_id IS DISTINCT FROM CASE
        WHEN id IN ('b0000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000003')
            THEN 'bb000000-0000-0000-0000-000000000001'::uuid
        ELSE 'bb000000-0000-0000-0000-000000000002'::uuid
      END;

-- 6. Biến thể SKU (ProductSkus)
INSERT INTO store.product_skus (id, product_id, sku_code, option_label, regular_price, sku_status, created_at, updated_at) VALUES
    ('e0000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'AX88D-PRO-4U', '4U / G5 (Navy/Gold)', 4290000, 'ACTIVE', now(), now()),
    ('e0000000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000001', 'AX88D-PRO-3U', '3U / G5 (Navy/Gold)', 4290000, 'ACTIVE', now(), now()),
    ('e0000000-0000-0000-0000-000000000003', 'b0000000-0000-0000-0000-000000000002', 'RYUGA-2-4U', '4U / G5 (Tím/Đen)', 3850000, 'ACTIVE', now(), now()),
    ('e0000000-0000-0000-0000-000000000004', 'b0000000-0000-0000-0000-000000000002', 'RYUGA-2-3U', '3U / G5 (Tím/Đen)', 3850000, 'ACTIVE', now(), now()),
    ('e0000000-0000-0000-0000-000000000005', 'b0000000-0000-0000-0000-000000000003', 'SHB-65Z3-41', 'Size 41 (Trắng/Đỏ)', 2650000, 'ACTIVE', now(), now()),
    ('e0000000-0000-0000-0000-000000000006', 'b0000000-0000-0000-0000-000000000003', 'SHB-65Z3-42', 'Size 42 (Trắng/Đỏ)', 2650000, 'ACTIVE', now(), now())
ON CONFLICT (sku_code) DO NOTHING;

-- 6.1. Thông số kỹ thuật chuẩn hóa cho hai vợt demo cùng danh mục.
-- T52 Product Comparison dùng cùng spec_key để dựng matrix; giá trị được chọn
-- có cả dòng giống và khác nhau để kiểm chứng chế độ "chỉ hiện khác biệt".
-- Upsert chỉ tác động đúng hai demo Product IDs và chỉ ghi lại khi nội dung đổi.
INSERT INTO store.product_specifications (
    product_id, spec_key, display_name, spec_value, display_order, created_at, updated_at
) VALUES
    ('b0000000-0000-0000-0000-000000000001', 'weight_class', 'Phân hạng trọng lượng', '3U / 4U', 10, now(), now()),
    ('b0000000-0000-0000-0000-000000000001', 'balance', 'Điểm cân bằng', 'Nặng đầu', 20, now(), now()),
    ('b0000000-0000-0000-0000-000000000001', 'shaft_stiffness', 'Độ cứng thân vợt', 'Cứng', 30, now(), now()),
    ('b0000000-0000-0000-0000-000000000001', 'max_tension', 'Sức căng tối đa', '28 lbs', 40, now(), now()),
    ('b0000000-0000-0000-0000-000000000001', 'material', 'Vật liệu', 'HM Graphite / Volume Cut Resin / Tungsten', 50, now(), now()),
    ('b0000000-0000-0000-0000-000000000001', 'play_style', 'Phong cách chơi', 'Tấn công tuyến sau', 60, now(), now()),
    ('b0000000-0000-0000-0000-000000000002', 'weight_class', 'Phân hạng trọng lượng', '3U / 4U', 10, now(), now()),
    ('b0000000-0000-0000-0000-000000000002', 'balance', 'Điểm cân bằng', 'Nặng đầu', 20, now(), now()),
    ('b0000000-0000-0000-0000-000000000002', 'shaft_stiffness', 'Độ cứng thân vợt', 'Trung bình cứng', 30, now(), now()),
    ('b0000000-0000-0000-0000-000000000002', 'max_tension', 'Sức căng tối đa', '31 lbs', 40, now(), now()),
    ('b0000000-0000-0000-0000-000000000002', 'material', 'Vật liệu', 'High Resilience Modulus Graphite / PYROFIL / WES 2.0', 50, now(), now()),
    ('b0000000-0000-0000-0000-000000000002', 'play_style', 'Phong cách chơi', 'Công thủ toàn diện', 60, now(), now())
ON CONFLICT (product_id, spec_key) DO UPDATE SET
    display_name = EXCLUDED.display_name,
    spec_value = EXCLUDED.spec_value,
    display_order = EXCLUDED.display_order,
    updated_at = now()
WHERE store.product_specifications.display_name IS DISTINCT FROM EXCLUDED.display_name
   OR store.product_specifications.spec_value IS DISTINCT FROM EXCLUDED.spec_value
   OR store.product_specifications.display_order IS DISTINCT FROM EXCLUDED.display_order;

-- 7. Số dư tồn kho ban đầu (InventoryBalances)
-- physical_qty = available_qty + reserved_qty + allocated_qty
INSERT INTO store.inventory_balances (id, product_sku_id, physical_qty, available_qty, reserved_qty, allocated_qty, revision, created_at, updated_at) VALUES
    (gen_random_uuid(), 'e0000000-0000-0000-0000-000000000001', 50, 50, 0, 0, 0, now(), now()),
    (gen_random_uuid(), 'e0000000-0000-0000-0000-000000000002', 30, 30, 0, 0, 0, now(), now()),
    (gen_random_uuid(), 'e0000000-0000-0000-0000-000000000003', 40, 40, 0, 0, 0, now(), now()),
    (gen_random_uuid(), 'e0000000-0000-0000-0000-000000000004', 20, 20, 0, 0, 0, now(), now()),
    (gen_random_uuid(), 'e0000000-0000-0000-0000-000000000005', 25, 25, 0, 0, 0, now(), now()),
    (gen_random_uuid(), 'e0000000-0000-0000-0000-000000000006', 25, 25, 0, 0, 0, now(), now())
ON CONFLICT (product_sku_id) DO NOTHING;

-- 7.1. Một đơn đã giao và review đại diện giúp demo/release gate kiểm tra
-- rating facet và best-selling mà không phụ thuộc dữ liệu từ E2E khác.
INSERT INTO store.sales_orders (
    id, customer_id, shipping_address_id, order_number, payment_method, order_status,
    shipping_address_snapshot, total_amount, subtotal_amount, voucher_discount_amount,
    deadline_at, confirmed_at, created_at, updated_at
) VALUES (
    'ab000000-0000-0000-0000-000000000001',
    'a0000000-0000-0000-0000-000000000003',
    'd0000000-0000-0000-0000-000000000001',
    'DEMO-SEARCH-0001', 'COD', 'COMPLETED',
    jsonb_build_object('recipientName', 'Trần Thị Khách', 'province', 'TP. Hồ Chí Minh'),
    4290000, 4290000, 0,
    now() + interval '1 day', now() - interval '2 days',
    now() - interval '3 days', now() - interval '2 days'
)
ON CONFLICT DO NOTHING;

INSERT INTO store.sales_order_items (
    id, order_id, product_sku_id, product_title_snapshot, sku_code_snapshot,
    sku_option_label_snapshot, quantity, unit_price, created_at, updated_at
) VALUES (
    'ab100000-0000-0000-0000-000000000001',
    'ab000000-0000-0000-0000-000000000001',
    'e0000000-0000-0000-0000-000000000001',
    'Vợt cầu lông Yonex Astrox 88D Pro', 'AX88D-PRO-4U',
    '4U / G5 (Navy/Gold)', 1, 4290000,
    now() - interval '3 days', now() - interval '2 days'
)
ON CONFLICT DO NOTHING;

INSERT INTO store.shipments (
    id, order_id, shipment_status, delivered_at, created_at, updated_at
) VALUES (
    'ab200000-0000-0000-0000-000000000001',
    'ab000000-0000-0000-0000-000000000001',
    'DELIVERED', now() - interval '2 days',
    now() - interval '3 days', now() - interval '2 days'
)
ON CONFLICT DO NOTHING;

INSERT INTO store.product_reviews (
    id, customer_id, product_id, qualifying_order_item_id,
    rating, content, review_status, created_at, updated_at
) VALUES (
    'ab300000-0000-0000-0000-000000000001',
    'a0000000-0000-0000-0000-000000000003',
    'b0000000-0000-0000-0000-000000000001',
    'ab100000-0000-0000-0000-000000000001',
    5, 'Sản phẩm demo chất lượng tốt và phù hợp kiểm thử tìm kiếm.', 'PUBLISHED',
    now() - interval '1 day', now() - interval '1 day'
)
ON CONFLICT DO NOTHING;

-- 8. Ảnh sản phẩm demo (ProductImages)
INSERT INTO store.product_images (id, product_id, file_key, image_status, alt_text, display_order, created_at, updated_at) VALUES
    ('f0000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'badminton-hero-v2.webp', 'ACTIVE', 'Vợt cầu lông Yonex Astrox 88D Pro', 1, now(), now()),
    ('f0000000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000002', 'badminton-hero-v2.webp', 'ACTIVE', 'Vợt cầu lông Victor Thruster Ryuga II', 1, now(), now()),
    ('f0000000-0000-0000-0000-000000000003', 'b0000000-0000-0000-0000-000000000003', 'badminton-hero-v2.webp', 'ACTIVE', 'Giày cầu lông Yonex Power Cushion 65 Z3', 1, now(), now())
ON CONFLICT (id) DO NOTHING;

-- 9. Phương thức giao hàng tối thiểu cho demo/E2E.
-- Tạo ở trạng thái INACTIVE trước để không vi phạm unique partial index nếu hệ thống
-- đã có một phương thức mặc định khác. Chỉ nâng thành mặc định khi chưa có default.
INSERT INTO store.shipping_methods (
    id, code, name, description, method_status, base_fee,
    free_shipping_threshold, estimated_min_days, estimated_max_days,
    is_default, display_order, revision, created_by, updated_by,
    created_at, updated_at
) VALUES (
    '90000000-0000-0000-0000-000000000010',
    'DEMO_STANDARD',
    'Giao hàng tiêu chuẩn',
    'Phương thức mặc định cho môi trường demo và kiểm thử E2E.',
    'INACTIVE',
    30000,
    5000000,
    2,
    4,
    false,
    10,
    1,
    'a0000000-0000-0000-0000-000000000001',
    'a0000000-0000-0000-0000-000000000001',
    now(),
    now()
)
ON CONFLICT (code) DO NOTHING;

UPDATE store.shipping_methods
SET method_status = 'ACTIVE',
    is_default = true,
    updated_by = 'a0000000-0000-0000-0000-000000000001',
    updated_at = now(),
    revision = revision + 1
WHERE code = 'DEMO_STANDARD'
  AND NOT EXISTS (
      SELECT 1
      FROM store.shipping_methods
      WHERE method_status = 'ACTIVE'
        AND is_default
  );

COMMIT;

