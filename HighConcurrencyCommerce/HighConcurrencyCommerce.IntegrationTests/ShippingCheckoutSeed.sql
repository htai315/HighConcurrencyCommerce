INSERT INTO store.shipping_methods(
    id, code, name, description, method_status, base_fee, free_shipping_threshold,
    estimated_min_days, estimated_max_days, is_default, display_order, revision,
    created_by, updated_by)
VALUES (
    '90000000-0000-0000-0000-000000000001',
    'TEST_FREE', 'Giao hàng test miễn phí', 'Chỉ dùng trong integration tests',
    'ACTIVE', 0, NULL, 1, 3, true, 0, 1,
    '00000000-0000-0000-0000-000000000002',
    '00000000-0000-0000-0000-000000000002')
ON CONFLICT (id) DO NOTHING;
