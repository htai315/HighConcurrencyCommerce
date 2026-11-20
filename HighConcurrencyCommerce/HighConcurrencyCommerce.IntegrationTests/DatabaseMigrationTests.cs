using HighConcurrencyCommerce.Infrastructure.Migrations;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class DatabaseMigrationTests
{
    [Fact]
    public Task Validation_rejects_an_unmigrated_database() => WithDatabaseAsync(async connectionString =>
    {
        var failure = await Assert.ThrowsAsync<DatabaseMigrationException>(
            () => CreateRunner(connectionString).ValidateAsync());

        Assert.Contains("--migrate-database", failure.Message, StringComparison.Ordinal);
    });

    [Fact]
    public Task Empty_database_is_migrated_and_replay_is_idempotent() => WithDatabaseAsync(async connectionString =>
    {
        var runner = CreateRunner(connectionString);
        var migrationCount = EmbeddedDatabaseMigrations.Load().Count;

        var first = await runner.RunAsync();
        var second = await runner.RunAsync();
        await runner.ValidateAsync();

        Assert.Equal(migrationCount, first.AppliedCount);
        Assert.Equal(0, first.BaselineCount);
        Assert.Equal(0, second.AppliedCount);
        Assert.Equal(migrationCount, second.AlreadyAppliedCount);
        Assert.Equal(migrationCount, await ScalarAsync<int>(connectionString, "SELECT count(*)::int FROM public.schema_migrations"));
        Assert.True(await ScalarAsync<bool>(connectionString, "SELECT to_regclass('store.order_return_requests') IS NOT NULL"));
        Assert.True(await ScalarAsync<bool>(connectionString, "SELECT to_regclass('store.shipping_methods') IS NOT NULL"));
        Assert.True(await ScalarAsync<bool>(connectionString, "SELECT to_regclass('store.shipping_method_province_rates') IS NOT NULL"));
        Assert.True(await ScalarAsync<bool>(connectionString, "SELECT to_regclass('store.carriers') IS NOT NULL"));
        Assert.True(await ScalarAsync<bool>(connectionString, "SELECT to_regclass('store.carrier_consignments') IS NOT NULL"));
        Assert.True(await ScalarAsync<bool>(connectionString, "SELECT to_regclass('store.carrier_tracking_events') IS NOT NULL"));
        Assert.True(await ScalarAsync<bool>(connectionString, "SELECT to_regclass('store.ix_products_search_title_trgm') IS NOT NULL"));
        Assert.True(await ScalarAsync<bool>(connectionString, "SELECT to_regclass('store.ix_product_skus_catalog_price') IS NOT NULL"));
        Assert.True(await ScalarAsync<bool>(connectionString, "SELECT to_regclass('store.product_alert_refresh_queue') IS NOT NULL"));
        Assert.True(await ScalarAsync<bool>(connectionString, "SELECT to_regclass('store.commerce_email_outbox') IS NOT NULL"));
        Assert.True(await ScalarAsync<bool>(connectionString, "SELECT to_regclass('store.ux_payment_attempt_active') IS NOT NULL"));
    });

    [Fact]
    public Task Legacy_database_without_history_is_baselined_and_upgraded() => WithDatabaseAsync(async connectionString =>
    {
        var migrations = EmbeddedDatabaseMigrations.Load();
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            foreach (var migration in migrations.Where(m => string.CompareOrdinal(m.Version, "009") <= 0))
            {
                await using var command = new NpgsqlCommand(migration.Sql, connection) { CommandTimeout = 300 };
                await command.ExecuteNonQueryAsync();
            }
            await using var legacy = new NpgsqlCommand("""
                INSERT INTO store.user_accounts(id,role_id,email,password_hash,full_name)
                VALUES('10000000-0000-0000-0000-000000000001',(SELECT id FROM store.roles WHERE role_key='CUSTOMER'),'legacy-shipping@example.com','x','Legacy Shipping');
                INSERT INTO store.customer_addresses(id,customer_id,recipient_name,phone,address_line,province,is_default)
                VALUES('10000000-0000-0000-0000-000000000002','10000000-0000-0000-0000-000000000001','Legacy Shipping','0900000000','1 Legacy','TP. HỒ CHÍ MINH',true);
                INSERT INTO store.sales_orders(id,customer_id,shipping_address_id,order_number,payment_method,order_status,shipping_address_snapshot,total_amount,deadline_at)
                VALUES('10000000-0000-0000-0000-000000000003','10000000-0000-0000-0000-000000000001','10000000-0000-0000-0000-000000000002','HC-LEGACY-SHIPPING','COD','PENDING_COD_CONFIRMATION','{}',123000,now()+interval '1 day');
                INSERT INTO store.shipments(id, order_id, shipment_status)
                VALUES('10000000-0000-0000-0000-000000000004','10000000-0000-0000-0000-000000000003','PENDING_PACKING');
                """, connection);
            await legacy.ExecuteNonQueryAsync();
        }

        var result = await CreateRunner(connectionString).RunAsync();

        Assert.Equal(1, result.BaselineCount);
        Assert.Equal(migrations.Count - result.BaselineCount, result.AppliedCount);
        Assert.Equal("BASELINE", await ScalarAsync<string>(connectionString,
            "SELECT execution_mode FROM public.schema_migrations WHERE version = '001'"));
        Assert.True(await ScalarAsync<bool>(connectionString, """
            SELECT EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'store' AND table_name = 'vouchers' AND column_name = 'name')
            """));
        Assert.True(await ScalarAsync<bool>(connectionString,
            "SELECT to_regclass('store.order_return_requests') IS NOT NULL"));
        Assert.True(await ScalarAsync<bool>(connectionString,
            "SELECT to_regclass('store.shipping_methods') IS NOT NULL"));
        Assert.True(await ScalarAsync<bool>(connectionString, """
            SELECT shipping_method_code_snapshot = 'LEGACY_FREE'
               AND shipping_method_name_snapshot = 'Miễn phí (đơn lịch sử)'
               AND shipping_fee = 0
               AND total_amount = 123000
            FROM store.sales_orders WHERE id='10000000-0000-0000-0000-000000000003'
            """));
        Assert.True(await ScalarAsync<bool>(connectionString, """
            SELECT EXISTS (
                SELECT 1 FROM store.shipments
                WHERE id='10000000-0000-0000-0000-000000000004')
            AND NOT EXISTS (
                SELECT 1 FROM store.carrier_consignments
                WHERE sales_shipment_id='10000000-0000-0000-0000-000000000004')
            """));
        Assert.True(await ScalarAsync<bool>(connectionString, """
            SELECT EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'store'
                  AND table_name = 'products'
                  AND column_name = 'search_title_norm'
                  AND is_generated = 'ALWAYS')
            """));
    });

    [Fact]
    public Task Reconciliation_assignment_migration_preserves_legacy_in_review_rows() => WithDatabaseAsync(async connectionString =>
    {
        var migrations = EmbeddedDatabaseMigrations.Load();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var core = new NpgsqlCommand(migrations.Single(x => x.Version == "001").Sql, connection)
                     { CommandTimeout = 300 })
            await core.ExecuteNonQueryAsync();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO store.roles(id,role_key,display_name)
            VALUES('40000000-0000-0000-0000-000000000000','CUSTOMER','Customer');
            INSERT INTO store.user_accounts(id,role_id,email,password_hash,full_name)
            VALUES('40000000-0000-0000-0000-000000000001',
                   (SELECT id FROM store.roles WHERE role_key='CUSTOMER'),
                   'legacy-reconciliation@example.invalid','x','Legacy Reconciliation');
            INSERT INTO store.sales_orders(id,customer_id,order_number,payment_method,order_status,shipping_address_snapshot,total_amount,deadline_at)
            VALUES('40000000-0000-0000-0000-000000000002','40000000-0000-0000-0000-000000000001',
                   'HC-LEGACY-RECON','PAYOS','EXPIRED','{}',1000,now()+interval '1 day');
            INSERT INTO store.payments(id,order_id,payment_method,expected_amount,payment_status)
            VALUES('40000000-0000-0000-0000-000000000003','40000000-0000-0000-0000-000000000002','PAYOS',1000,'VOID');
            INSERT INTO store.payment_reconciliation_cases(id,payment_id,reason,case_status)
            VALUES('40000000-0000-0000-0000-000000000004','40000000-0000-0000-0000-000000000003','OTHER','IN_REVIEW');
            """, connection))
            await seed.ExecuteNonQueryAsync();
        await using (var migration = new NpgsqlCommand(migrations.Single(x => x.Version == "023").Sql, connection)
                     { CommandTimeout = 300 })
            await migration.ExecuteNonQueryAsync();

        await using var verify = new NpgsqlCommand("""
            SELECT c.case_status,c.assigned_to IS NULL,NOT p.convalidated
            FROM store.payment_reconciliation_cases c
            CROSS JOIN pg_constraint p
            WHERE c.id='40000000-0000-0000-0000-000000000004'
              AND p.conname='ck_reconciliation_assignment'
              AND p.conrelid='store.payment_reconciliation_cases'::regclass
            """, connection);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("IN_REVIEW", reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
    });

    [Fact]
    public Task Wishlist_product_alert_migration_is_replay_safe_and_queues_relevant_changes() => WithDatabaseAsync(async connectionString =>
    {
        await CreateRunner(connectionString).RunAsync();
        await ExecuteAsync(connectionString, """
            INSERT INTO store.user_accounts(id, role_id, email, password_hash, full_name, email_verified_at)
            VALUES
                ('51000000-0000-0000-0000-000000000001',
                 (SELECT id FROM store.roles WHERE role_key='STAFF'),
                 'alert-staff@example.com', 'x', 'Alert Staff', now()),
                ('51000000-0000-0000-0000-000000000002',
                 (SELECT id FROM store.roles WHERE role_key='CUSTOMER'),
                 'alert-customer@example.com', 'x', 'Alert Customer', now());
            INSERT INTO store.products(id, title, publication_status, created_by, published_by, published_at)
            VALUES('51000000-0000-0000-0000-000000000003', 'Alert Product', 'PUBLISHED',
                   '51000000-0000-0000-0000-000000000001',
                   '51000000-0000-0000-0000-000000000001', now());
            INSERT INTO store.product_skus(id, product_id, sku_code, option_label, regular_price, sku_status)
            VALUES('51000000-0000-0000-0000-000000000004',
                   '51000000-0000-0000-0000-000000000003', 'ALERT-SKU-1', '4U/G5', 2000000, 'ACTIVE');
            INSERT INTO store.inventory_balances(
                id, product_sku_id, physical_qty, available_qty, reserved_qty, allocated_qty)
            VALUES('51000000-0000-0000-0000-000000000005',
                   '51000000-0000-0000-0000-000000000004', 0, 0, 0, 0);
            INSERT INTO store.customer_wishlist_items(customer_id, product_id, created_at)
            VALUES('51000000-0000-0000-0000-000000000002',
                   '51000000-0000-0000-0000-000000000003', '2026-01-02T03:04:05Z');
            DELETE FROM store.product_alert_refresh_queue;
            """);

        Assert.True(await ScalarAsync<bool>(connectionString, """
            SELECT NOT back_in_stock_enabled
               AND NOT price_drop_enabled
               AND NOT email_enabled
               AND alert_sequence = 0
               AND alert_revision = 0
               AND created_at = '2026-01-02T03:04:05Z'::timestamptz
            FROM store.customer_wishlist_items
            WHERE customer_id='51000000-0000-0000-0000-000000000002'
              AND product_id='51000000-0000-0000-0000-000000000003'
            """));

        await ExecuteAsync(connectionString, """
            UPDATE store.inventory_balances
            SET physical_qty=5, available_qty=5
            WHERE product_sku_id='51000000-0000-0000-0000-000000000004'
            """);
        Assert.Equal(1, await ScalarAsync<int>(connectionString, """
            SELECT reason_mask FROM store.product_alert_refresh_queue
            WHERE product_id='51000000-0000-0000-0000-000000000003'
            """));
        Assert.Equal(1L, await ScalarAsync<long>(connectionString, """
            SELECT event_version FROM store.product_alert_refresh_queue
            WHERE product_id='51000000-0000-0000-0000-000000000003'
            """));

        await ExecuteAsync(connectionString, """
            UPDATE store.product_skus SET regular_price=1800000
            WHERE id='51000000-0000-0000-0000-000000000004';
            UPDATE store.product_skus SET sku_status='INACTIVE'
            WHERE id='51000000-0000-0000-0000-000000000004';
            """);
        Assert.True(await ScalarAsync<bool>(connectionString, """
            SELECT reason_mask = 7 AND event_version = 3
            FROM store.product_alert_refresh_queue
            WHERE product_id='51000000-0000-0000-0000-000000000003'
            """));

        await ExecuteAsync(connectionString, """
            UPDATE store.products SET title='Unrelated title change'
            WHERE id='51000000-0000-0000-0000-000000000003'
            """);
        Assert.Equal(3L, await ScalarAsync<long>(connectionString, """
            SELECT event_version FROM store.product_alert_refresh_queue
            WHERE product_id='51000000-0000-0000-0000-000000000003'
            """));

        await ExecuteAsync(connectionString, """
            UPDATE store.products SET publication_status='SUSPENDED'
            WHERE id='51000000-0000-0000-0000-000000000003'
            """);
        Assert.Equal(4L, await ScalarAsync<long>(connectionString, """
            SELECT event_version FROM store.product_alert_refresh_queue
            WHERE product_id='51000000-0000-0000-0000-000000000003'
            """));

        await ExecuteAsync(connectionString, "DELETE FROM store.product_alert_refresh_queue");
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            await using var command = new NpgsqlCommand("""
                UPDATE store.inventory_balances
                SET physical_qty=6, available_qty=6
                WHERE product_sku_id='51000000-0000-0000-0000-000000000004'
                """, connection, tx);
            await command.ExecuteNonQueryAsync();
            await tx.RollbackAsync();
        }
        Assert.False(await ScalarAsync<bool>(connectionString,
            "SELECT EXISTS(SELECT 1 FROM store.product_alert_refresh_queue)"));

        var channelConstraint = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString, """
            UPDATE store.customer_wishlist_items
            SET email_enabled=true
            WHERE customer_id='51000000-0000-0000-0000-000000000002'
              AND product_id='51000000-0000-0000-0000-000000000003'
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, channelConstraint.SqlState);
        Assert.Equal("ck_customer_wishlist_alert_channel", channelConstraint.ConstraintName);

        var actionUrlConstraint = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString, """
            INSERT INTO store.in_app_notifications(
                recipient_id, event_key, notification_type, title, body, action_url)
            VALUES('51000000-0000-0000-0000-000000000002', 'bad-url', 'TEST', 'Test', 'Test', 'https://evil.example')
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, actionUrlConstraint.SqlState);
        Assert.Equal("ck_in_app_notifications_action_url", actionUrlConstraint.ConstraintName);

        var migration = EmbeddedDatabaseMigrations.Load().Single(m => m.Version == "021");
        await ExecuteAsync(connectionString, migration.Sql);
        Assert.True(await ScalarAsync<bool>(connectionString, """
            SELECT count(*) = 1
               AND bool_and(created_at = '2026-01-02T03:04:05Z'::timestamptz)
            FROM store.customer_wishlist_items
            WHERE customer_id='51000000-0000-0000-0000-000000000002'
              AND product_id='51000000-0000-0000-0000-000000000003'
            """));
    });

    [Fact]
    public Task Catalog_search_migration_normalizes_generated_columns_and_is_replay_safe() => WithDatabaseAsync(async connectionString =>
    {
        await CreateRunner(connectionString).RunAsync();
        await ExecuteAsync(connectionString, """
            INSERT INTO store.user_accounts(id, role_id, email, password_hash, full_name, email_verified_at)
            VALUES(
                '40000000-0000-0000-0000-000000000001',
                (SELECT id FROM store.roles WHERE role_key = 'STAFF'),
                'search-staff@example.com', 'x', 'Search Staff', now());
            INSERT INTO store.categories(id, name, slug, is_visible)
            VALUES('40000000-0000-0000-0000-000000000002', '  Vợt   Cầu Lông  ', 'vot-cau-long-search', true);
            INSERT INTO store.brands(id, name, slug, is_active)
            VALUES('40000000-0000-0000-0000-000000000003', 'YONEX Nhật Bản', 'yonex-search', true);
            INSERT INTO store.products(
                id, category_id, brand_id, title, publication_status, created_by, published_by, published_at)
            VALUES(
                '40000000-0000-0000-0000-000000000004',
                '40000000-0000-0000-0000-000000000002',
                '40000000-0000-0000-0000-000000000003',
                '  Vợt Cầu Lông   YONEX ÁSTRÓX  ', 'PUBLISHED',
                '40000000-0000-0000-0000-000000000001',
                '40000000-0000-0000-0000-000000000001', now());
            INSERT INTO store.product_skus(id, product_id, sku_code, option_label, regular_price, sku_status)
            VALUES(
                '40000000-0000-0000-0000-000000000005',
                '40000000-0000-0000-0000-000000000004',
                'AX88D-PRO', '  4U / G5 (Đỏ) ', 3490000, 'ACTIVE');
            """);

        Assert.True(await ScalarAsync<bool>(connectionString, """
            SELECT p.search_title_norm = 'vot cau long yonex astrox'
               AND s.search_sku_norm = 'ax88d-pro'
               AND s.search_option_norm = '4u / g5 (do)'
               AND b.search_name_norm = 'yonex nhat ban'
               AND c.search_name_norm = 'vot cau long'
            FROM store.products p
            JOIN store.product_skus s ON s.product_id = p.id
            JOIN store.brands b ON b.id = p.brand_id
            JOIN store.categories c ON c.id = p.category_id
            WHERE p.id = '40000000-0000-0000-0000-000000000004'
            """));

        await ExecuteAsync(connectionString, """
            UPDATE store.products
            SET title = '  Giày   Cầu Lông  Cao Cấp '
            WHERE id = '40000000-0000-0000-0000-000000000004'
            """);
        Assert.Equal("giay cau long cao cap", await ScalarAsync<string>(connectionString, """
            SELECT search_title_norm FROM store.products
            WHERE id = '40000000-0000-0000-0000-000000000004'
            """));

        var searchMigration = EmbeddedDatabaseMigrations.Load().Single(m => m.Version == "020");
        await ExecuteAsync(connectionString, searchMigration.Sql);

        Assert.Equal(1, await ScalarAsync<int>(connectionString, """
            SELECT count(*)::int FROM store.products
            WHERE id = '40000000-0000-0000-0000-000000000004'
            """));
        Assert.True(await ScalarAsync<bool>(connectionString, """
            SELECT to_regclass('store.ix_products_search_title_trgm') IS NOT NULL
               AND to_regclass('store.ix_product_skus_search_sku_trgm') IS NOT NULL
               AND to_regclass('store.ix_product_skus_search_option_trgm') IS NOT NULL
               AND to_regclass('store.ix_brands_search_name_trgm') IS NOT NULL
               AND to_regclass('store.ix_categories_search_name_trgm') IS NOT NULL
               AND to_regclass('store.ix_product_skus_catalog_price') IS NOT NULL
            """));
        Assert.Equal("i", await ScalarAsync<string>(connectionString, """
            SELECT p.provolatile::text
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'store' AND p.proname = 'catalog_search_normalize'
            """));
    });

    [Fact]
    public Task Shipping_migration_enforces_the_order_total_formula() => WithDatabaseAsync(async connectionString =>
    {
        await CreateRunner(connectionString).RunAsync();
        await ExecuteAsync(connectionString, """
            INSERT INTO store.user_accounts(id, role_id, email, password_hash, full_name, email_verified_at)
            VALUES (
                '20000000-0000-0000-0000-000000000001',
                (SELECT id FROM store.roles WHERE role_key = 'CUSTOMER'),
                'shipping-formula@example.com', 'x', 'Shipping Formula', now());
            INSERT INTO store.customer_addresses(id, customer_id, recipient_name, phone, address_line, province, is_default)
            VALUES (
                '20000000-0000-0000-0000-000000000002',
                '20000000-0000-0000-0000-000000000001',
                'Shipping Formula', '0900000001', '2 Formula', 'TP. HỒ CHÍ MINH', true);
            INSERT INTO store.sales_orders(
                id, customer_id, shipping_address_id, order_number, payment_method, order_status,
                shipping_address_snapshot, subtotal_amount, voucher_discount_amount, shipping_fee,
                shipping_method_code_snapshot, shipping_method_name_snapshot, total_amount, deadline_at)
            VALUES (
                '20000000-0000-0000-0000-000000000003',
                '20000000-0000-0000-0000-000000000001',
                '20000000-0000-0000-0000-000000000002',
                'HC-SHIPPING-FORMULA-OK', 'COD', 'PENDING_COD_CONFIRMATION', '{}',
                100000, 10000, 20000, 'STANDARD', 'Giao hàng tiêu chuẩn', 110000, now() + interval '1 day');
            """);

        var failure = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString, """
            INSERT INTO store.sales_orders(
                id, customer_id, shipping_address_id, order_number, payment_method, order_status,
                shipping_address_snapshot, subtotal_amount, voucher_discount_amount, shipping_fee,
                shipping_method_code_snapshot, shipping_method_name_snapshot, total_amount, deadline_at)
            VALUES (
                '20000000-0000-0000-0000-000000000004',
                '20000000-0000-0000-0000-000000000001',
                '20000000-0000-0000-0000-000000000002',
                'HC-SHIPPING-FORMULA-BAD', 'COD', 'PENDING_COD_CONFIRMATION', '{}',
                100000, 10000, 20000, 'STANDARD', 'Giao hàng tiêu chuẩn', 90000, now() + interval '1 day');
            """));

        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        Assert.Equal("ck_sales_orders_shipping_amounts", failure.ConstraintName);
    });

    [Fact]
    public Task Carrier_tracking_migration_enforces_targets_tracking_and_event_idempotency() => WithDatabaseAsync(async connectionString =>
    {
        await CreateRunner(connectionString).RunAsync();
        await ExecuteAsync(connectionString, """
            INSERT INTO store.user_accounts(id, role_id, email, password_hash, full_name, email_verified_at)
            VALUES
                ('30000000-0000-0000-0000-000000000001', (SELECT id FROM store.roles WHERE role_key='ADMIN'), 'carrier-admin@example.com', 'x', 'Carrier Admin', now()),
                ('30000000-0000-0000-0000-000000000002', (SELECT id FROM store.roles WHERE role_key='CUSTOMER'), 'carrier-customer@example.com', 'x', 'Carrier Customer', now());
            INSERT INTO store.customer_addresses(id, customer_id, recipient_name, phone, address_line, province, is_default)
            VALUES('30000000-0000-0000-0000-000000000003', '30000000-0000-0000-0000-000000000002', 'Carrier Customer', '0900000002', '3 Tracking', 'Hồ Chí Minh', true);
            INSERT INTO store.sales_orders(
                id, customer_id, shipping_address_id, order_number, payment_method, order_status,
                shipping_address_snapshot, subtotal_amount, voucher_discount_amount, shipping_fee,
                shipping_method_code_snapshot, shipping_method_name_snapshot, total_amount, deadline_at)
            VALUES
                ('30000000-0000-0000-0000-000000000004', '30000000-0000-0000-0000-000000000002', '30000000-0000-0000-0000-000000000003',
                 'HC-CARRIER-001', 'COD', 'FULFILLING', '{}', 100000, 0, 30000, 'STANDARD', 'Tiêu chuẩn', 130000, now()+interval '1 day'),
                ('30000000-0000-0000-0000-000000000005', '30000000-0000-0000-0000-000000000002', '30000000-0000-0000-0000-000000000003',
                 'HC-CARRIER-002', 'COD', 'FULFILLING', '{}', 100000, 0, 30000, 'STANDARD', 'Tiêu chuẩn', 130000, now()+interval '1 day');
            INSERT INTO store.shipments(id, order_id, shipment_status)
            VALUES
                ('30000000-0000-0000-0000-000000000006', '30000000-0000-0000-0000-000000000004', 'PACKED'),
                ('30000000-0000-0000-0000-000000000007', '30000000-0000-0000-0000-000000000005', 'PACKED');
            INSERT INTO store.carriers(id, code, name, carrier_status, created_by, updated_by)
            VALUES('30000000-0000-0000-0000-000000000008', 'HCC_LOCAL', 'HCC Local Carrier', 'ACTIVE',
                   '30000000-0000-0000-0000-000000000001', '30000000-0000-0000-0000-000000000001');
            INSERT INTO store.carrier_consignments(
                id, sales_shipment_id, carrier_id, tracking_number, recipient_name_snapshot,
                recipient_phone_snapshot, destination_snapshot, shipping_method_code_snapshot,
                shipping_method_name_snapshot, created_by, updated_by)
            VALUES(
                '30000000-0000-0000-0000-000000000009', '30000000-0000-0000-0000-000000000006',
                '30000000-0000-0000-0000-000000000008', 'HCC-TRACK-001', 'Carrier Customer',
                '0900000002', '3 Tracking, Hồ Chí Minh', 'STANDARD', 'Tiêu chuẩn',
                '30000000-0000-0000-0000-000000000001', '30000000-0000-0000-0000-000000000001');
            INSERT INTO store.carrier_tracking_events(
                id, consignment_id, event_key, event_type, event_status, occurred_at,
                source_type, actor_id, payload_hash)
            VALUES(
                '30000000-0000-0000-0000-000000000010', '30000000-0000-0000-0000-000000000009',
                'created:1', 'CONSIGNMENT_CREATED', 'DRAFT', now(), 'STAFF',
                '30000000-0000-0000-0000-000000000001', repeat('a', 64));
            """);

        var xorFailure = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString, """
            INSERT INTO store.carrier_consignments(
                id, carrier_id, tracking_number, recipient_name_snapshot, recipient_phone_snapshot,
                destination_snapshot, created_by, updated_by)
            VALUES(
                '30000000-0000-0000-0000-000000000011', '30000000-0000-0000-0000-000000000008',
                'HCC-XOR-001', 'Carrier Customer', '0900000002', '3 Tracking, Hồ Chí Minh',
                '30000000-0000-0000-0000-000000000001', '30000000-0000-0000-0000-000000000001');
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, xorFailure.SqlState);
        Assert.Equal("ck_carrier_consignments_target_xor", xorFailure.ConstraintName);

        var trackingFailure = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString, """
            INSERT INTO store.carrier_consignments(
                id, sales_shipment_id, carrier_id, tracking_number, recipient_name_snapshot,
                recipient_phone_snapshot, destination_snapshot, created_by, updated_by)
            VALUES(
                '30000000-0000-0000-0000-000000000012', '30000000-0000-0000-0000-000000000007',
                '30000000-0000-0000-0000-000000000008', 'hcc-track-001', 'Carrier Customer',
                '0900000002', '3 Tracking, Hồ Chí Minh', '30000000-0000-0000-0000-000000000001',
                '30000000-0000-0000-0000-000000000001');
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, trackingFailure.SqlState);
        Assert.Equal("ux_carrier_consignments_tracking", trackingFailure.ConstraintName);

        var eventFailure = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString, """
            INSERT INTO store.carrier_tracking_events(
                id, consignment_id, event_key, event_type, event_status, occurred_at,
                source_type, actor_id, payload_hash)
            VALUES(
                '30000000-0000-0000-0000-000000000013', '30000000-0000-0000-0000-000000000009',
                'created:1', 'CONSIGNMENT_CREATED', 'DRAFT', now(), 'STAFF',
                '30000000-0000-0000-0000-000000000001', repeat('a', 64));
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, eventFailure.SqlState);
        Assert.Equal("ux_carrier_tracking_event_key", eventFailure.ConstraintName);
    });

    [Fact]
    public Task Concurrent_migrators_are_serialized_by_advisory_lock() => WithDatabaseAsync(async connectionString =>
    {
        var first = CreateRunner(connectionString);
        var second = CreateRunner(connectionString);
        var migrationCount = EmbeddedDatabaseMigrations.Load().Count;

        var results = await Task.WhenAll(first.RunAsync(), second.RunAsync());

        Assert.Equal(migrationCount, results.Sum(result => result.AppliedCount));
        Assert.Equal(migrationCount, results.Sum(result => result.AlreadyAppliedCount));
        Assert.Equal(migrationCount, await ScalarAsync<int>(connectionString,
            "SELECT count(*)::int FROM public.schema_migrations"));
    });

    [Fact]
    public Task Validation_rejects_checksum_drift_and_unknown_versions() => WithDatabaseAsync(async connectionString =>
    {
        var runner = CreateRunner(connectionString);
        await runner.RunAsync();

        await ExecuteAsync(connectionString,
            "UPDATE public.schema_migrations SET checksum = repeat('A', 64) WHERE version = '010'");
        var drift = await Assert.ThrowsAsync<DatabaseMigrationException>(() => runner.ValidateAsync());
        Assert.Contains("Checksum migration 010", drift.Message, StringComparison.Ordinal);

        var expectedChecksum = EmbeddedDatabaseMigrations.Load().Single(m => m.Version == "010").Checksum;
        await ExecuteAsync(connectionString,
            "UPDATE public.schema_migrations SET checksum = @checksum WHERE version = '010'",
            new NpgsqlParameter("checksum", expectedChecksum));
        await ExecuteAsync(connectionString, """
            INSERT INTO public.schema_migrations
                (version, description, checksum, execution_ms, application_version, execution_mode)
            VALUES ('999', 'unknown future migration', repeat('B', 64), 0, 'test', 'APPLIED')
            """);
        var downgrade = await Assert.ThrowsAsync<DatabaseMigrationException>(() => runner.ValidateAsync());
        Assert.Contains("999", downgrade.Message, StringComparison.Ordinal);
    });

    [Fact]
    public Task Failed_migration_is_not_recorded_and_can_be_retried() => WithDatabaseAsync(async connectionString =>
    {
        var firstMigration = DatabaseMigration.Create("001", "test core", """
            BEGIN;
            CREATE SCHEMA store;
            CREATE TABLE store.roles (id integer PRIMARY KEY);
            COMMIT;
            """);
        var brokenMigration = DatabaseMigration.Create("002", "broken", """
            BEGIN;
            CREATE TABLE store.recovery_probe (id integer PRIMARY KEY);
            SELECT * FROM store.table_that_does_not_exist;
            COMMIT;
            """);
        var brokenRunner = new DatabaseMigrationRunner(
            connectionString,
            [firstMigration, brokenMigration],
            lockTimeout: TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<DatabaseMigrationException>(() => brokenRunner.RunAsync());
        Assert.Equal(1, await ScalarAsync<int>(connectionString,
            "SELECT count(*)::int FROM public.schema_migrations"));
        Assert.False(await ScalarAsync<bool>(connectionString,
            "SELECT to_regclass('store.recovery_probe') IS NOT NULL"));

        var repairedMigration = DatabaseMigration.Create("002", "repaired", """
            BEGIN;
            CREATE TABLE store.recovery_probe (id integer PRIMARY KEY);
            COMMIT;
            """);
        var repairedRunner = new DatabaseMigrationRunner(
            connectionString,
            [firstMigration, repairedMigration],
            lockTimeout: TimeSpan.FromSeconds(10));
        var result = await repairedRunner.RunAsync();

        Assert.Equal(1, result.AppliedCount);
        Assert.True(await ScalarAsync<bool>(connectionString,
            "SELECT to_regclass('store.recovery_probe') IS NOT NULL"));
        await repairedRunner.ValidateAsync();
    });

    private static DatabaseMigrationRunner CreateRunner(string connectionString) => new(
        connectionString,
        EmbeddedDatabaseMigrations.Load(),
        lockTimeout: TimeSpan.FromSeconds(30));

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Expected scalar value."));
    }

    private static async Task ExecuteAsync(
        string connectionString,
        string sql,
        params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WithDatabaseAsync(Func<string, Task> action)
    {
        var configuration = new ConfigurationBuilder().AddUserSecrets<Program>().Build();
        var configured = Environment.GetEnvironmentVariable("HCC_TEST_POSTGRES")
            ?? configuration.GetConnectionString("StoreDb")
            ?? throw new InvalidOperationException(
                "Set HCC_TEST_POSTGRES or API User Secrets ConnectionStrings:StoreDb for a LOCAL PostgreSQL role with CREATEDB.");
        var settings = new NpgsqlConnectionStringBuilder(configured);
        if (settings.Host is not ("localhost" or "127.0.0.1" or "::1"))
            throw new InvalidOperationException("Migration integration tests only allow loopback PostgreSQL hosts.");

        var databaseName = "hcc_migrations_" + Guid.NewGuid().ToString("N");
        settings.Database = "postgres";
        settings.Pooling = false;
        var adminConnection = settings.ConnectionString;
        await using (var admin = new NpgsqlConnection(adminConnection))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            settings.Database = databaseName;
            await action(settings.ConnectionString);
        }
        finally
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(databaseName, "^hcc_migrations_[0-9a-f]{32}$"))
                throw new InvalidOperationException("Refusing to drop a non-test database.");
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(adminConnection);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}

