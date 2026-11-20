using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Wishlist;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class ProductAlertEvaluationTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    [Fact]
    public async Task Stock_and_price_transitions_create_exactly_one_notification_and_outbox_each()
    {
        await ClearQueueAsync();
        var data = await SeedSubscriptionAsync(emailVerified: true, emailEnabled: true);
        await ExecuteAsync("""
            UPDATE store.inventory_balances
            SET physical_qty=4, available_qty=4
            WHERE product_sku_id=@skuId;
            UPDATE store.product_skus SET regular_price=1800000 WHERE id=@skuId;
            """, new NpgsqlParameter("skuId", data.SkuId));

        var worker = CreateWorker();
        Assert.Equal(1, await worker.ProcessBatchAsync());
        Assert.Equal(0, await worker.ProcessBatchAsync());

        Assert.Equal(2, await ScalarAsync<int>("""
            SELECT count(*)::int FROM store.in_app_notifications
            WHERE recipient_id=@customerId AND notification_type LIKE 'WISHLIST_%'
            """, new NpgsqlParameter("customerId", data.CustomerId)));
        Assert.Equal(2, await ScalarAsync<int>("""
            SELECT count(*)::int FROM store.commerce_email_outbox
            WHERE recipient_id=@customerId
            """, new NpgsqlParameter("customerId", data.CustomerId)));
        Assert.True(await ScalarAsync<bool>("""
            SELECT last_observed_in_stock
               AND last_observed_min_price=1800000
               AND alert_sequence=2
               AND last_notified_at IS NOT NULL
            FROM store.customer_wishlist_items
            WHERE customer_id=@customerId AND product_id=@productId
            """, new NpgsqlParameter("customerId", data.CustomerId), new NpgsqlParameter("productId", data.ProductId)));
        Assert.False(await ScalarAsync<bool>("""
            SELECT EXISTS(SELECT 1 FROM store.product_alert_refresh_queue WHERE product_id=@productId)
            """, new NpgsqlParameter("productId", data.ProductId)));

        var distinctKeys = await ScalarAsync<int>("""
            SELECT count(DISTINCT event_key)::int FROM store.in_app_notifications
            WHERE recipient_id=@customerId AND notification_type LIKE 'WISHLIST_%'
            """, new NpgsqlParameter("customerId", data.CustomerId));
        Assert.Equal(2, distinctKeys);
    }

    [Fact]
    public async Task Concurrent_workers_claim_disjoint_products_and_recover_expired_lease()
    {
        await ClearQueueAsync();
        var first = await SeedSubscriptionAsync(emailVerified: true, emailEnabled: false);
        var second = await SeedSubscriptionAsync(emailVerified: true, emailEnabled: false);
        await ExecuteAsync("""
            UPDATE store.inventory_balances SET physical_qty=1, available_qty=1
            WHERE product_sku_id IN (@firstSku, @secondSku);
            UPDATE store.product_alert_refresh_queue
            SET locked_at=now()-interval '6 minutes', locked_by='dead-worker'
            WHERE product_id=@firstProduct;
            """,
            new NpgsqlParameter("firstSku", first.SkuId),
            new NpgsqlParameter("secondSku", second.SkuId),
            new NpgsqlParameter("firstProduct", first.ProductId));

        var results = await Task.WhenAll(CreateWorker().ProcessBatchAsync(), CreateWorker().ProcessBatchAsync());
        Assert.Equal(2, results.Sum());
        Assert.Equal(2, await ScalarAsync<int>("""
            SELECT count(*)::int FROM store.in_app_notifications
            WHERE notification_type='WISHLIST_BACK_IN_STOCK'
              AND recipient_id IN (@firstCustomer, @secondCustomer)
            """, new NpgsqlParameter("firstCustomer", first.CustomerId), new NpgsqlParameter("secondCustomer", second.CustomerId)));
        Assert.Equal(0, await ScalarAsync<int>("""
            SELECT count(*)::int FROM store.product_alert_refresh_queue
            WHERE product_id IN (@firstProduct, @secondProduct)
            """, new NpgsqlParameter("firstProduct", first.ProductId), new NpgsqlParameter("secondProduct", second.ProductId)));
    }

    [Fact]
    public async Task New_event_version_during_evaluation_is_retained_and_lease_is_released()
    {
        await ClearQueueAsync();
        var data = await SeedSubscriptionAsync(emailVerified: true, emailEnabled: false);
        await ExecuteAsync("""
            UPDATE store.inventory_balances SET physical_qty=2, available_qty=2
            WHERE product_sku_id=@skuId
            """, new NpgsqlParameter("skuId", data.SkuId));

        var checkpoint = new MutatingCheckpoint(fixture, data.ProductId, data.SkuId);
        var worker = CreateWorker(checkpoint);
        Assert.Equal(1, await worker.ProcessBatchAsync());

        Assert.True(await ScalarAsync<bool>("""
            SELECT event_version=2 AND locked_at IS NULL AND locked_by IS NULL
            FROM store.product_alert_refresh_queue WHERE product_id=@productId
            """, new NpgsqlParameter("productId", data.ProductId)));
        Assert.Equal(1, await CreateWorker().ProcessBatchAsync());
        Assert.False(await ScalarAsync<bool>("""
            SELECT EXISTS(SELECT 1 FROM store.product_alert_refresh_queue WHERE product_id=@productId)
            """, new NpgsqlParameter("productId", data.ProductId)));
        Assert.Equal(1, await ScalarAsync<int>("""
            SELECT count(*)::int FROM store.in_app_notifications
            WHERE recipient_id=@customerId AND notification_type='WISHLIST_BACK_IN_STOCK'
            """, new NpgsqlParameter("customerId", data.CustomerId)));
    }

    [Fact]
    public async Task Unverified_email_at_delivery_time_keeps_in_app_but_skips_email_outbox()
    {
        await ClearQueueAsync();
        var data = await SeedSubscriptionAsync(emailVerified: true, emailEnabled: true);
        await ExecuteAsync("""
            UPDATE store.user_accounts SET email_verified_at=NULL WHERE id=@customerId;
            UPDATE store.inventory_balances SET physical_qty=1, available_qty=1
            WHERE product_sku_id=@skuId;
            """, new NpgsqlParameter("customerId", data.CustomerId), new NpgsqlParameter("skuId", data.SkuId));

        Assert.Equal(1, await CreateWorker().ProcessBatchAsync());
        Assert.Equal(1, await ScalarAsync<int>("""
            SELECT count(*)::int FROM store.in_app_notifications
            WHERE recipient_id=@customerId AND notification_type='WISHLIST_BACK_IN_STOCK'
            """, new NpgsqlParameter("customerId", data.CustomerId)));
        Assert.Equal(0, await ScalarAsync<int>("""
            SELECT count(*)::int FROM store.commerce_email_outbox WHERE recipient_id=@customerId
            """, new NpgsqlParameter("customerId", data.CustomerId)));
    }

    [Fact]
    public async Task Evaluation_failure_releases_lease_and_schedules_sanitized_retry()
    {
        await ClearQueueAsync();
        var data = await SeedSubscriptionAsync(emailVerified: true, emailEnabled: false);
        await ExecuteAsync("""
            UPDATE store.inventory_balances SET physical_qty=1, available_qty=1
            WHERE product_sku_id=@skuId
            """, new NpgsqlParameter("skuId", data.SkuId));

        Assert.Equal(1, await CreateWorker(new ThrowingCheckpoint()).ProcessBatchAsync());
        Assert.True(await ScalarAsync<bool>("""
            SELECT attempt_count=1
               AND locked_at IS NULL
               AND locked_by IS NULL
               AND next_attempt_at > now()
               AND last_error_code='InvalidOperationException'
            FROM store.product_alert_refresh_queue WHERE product_id=@productId
            """, new NpgsqlParameter("productId", data.ProductId)));
        Assert.Equal(0, await ScalarAsync<int>("""
            SELECT count(*)::int FROM store.in_app_notifications
            WHERE recipient_id=@customerId AND notification_type LIKE 'WISHLIST_%'
            """, new NpgsqlParameter("customerId", data.CustomerId)));
        Assert.True(ProductAlertEvaluationWorker.SanitizeErrorCode(
            new Exception(new string('x', 500))).Length <= 64);
    }

    [Fact]
    public async Task Multiple_sku_stock_updates_coalesce_into_one_business_alert()
    {
        await ClearQueueAsync();
        var data = await SeedSubscriptionAsync(emailVerified: true, emailEnabled: false);
        var secondSku = Guid.NewGuid();
        await ExecuteAsync("""
            INSERT INTO store.product_skus(id, product_id, sku_code, option_label, regular_price, sku_status)
            VALUES(@skuId, @productId, @skuCode, '3U/G5', 2100000, 'ACTIVE');
            INSERT INTO store.inventory_balances(
                id, product_sku_id, physical_qty, available_qty, reserved_qty, allocated_qty)
            VALUES(gen_random_uuid(), @skuId, 0, 0, 0, 0);
            DELETE FROM store.product_alert_refresh_queue WHERE product_id=@productId;
            """, new NpgsqlParameter("skuId", secondSku), new NpgsqlParameter("productId", data.ProductId),
            new NpgsqlParameter("skuCode", $"ALERT-SECOND-{secondSku:N}"));

        await Task.WhenAll(
            ExecuteAsync("UPDATE store.inventory_balances SET physical_qty=1, available_qty=1 WHERE product_sku_id=@skuId",
                new NpgsqlParameter("skuId", data.SkuId)),
            ExecuteAsync("UPDATE store.inventory_balances SET physical_qty=2, available_qty=2 WHERE product_sku_id=@skuId",
                new NpgsqlParameter("skuId", secondSku)));

        Assert.True(await ScalarAsync<bool>("""
            SELECT event_version=2 AND reason_mask=1
            FROM store.product_alert_refresh_queue WHERE product_id=@productId
            """, new NpgsqlParameter("productId", data.ProductId)));
        Assert.Equal(1, await CreateWorker().ProcessBatchAsync());
        Assert.Equal(1, await ScalarAsync<int>("""
            SELECT count(*)::int FROM store.in_app_notifications
            WHERE recipient_id=@customerId AND notification_type='WISHLIST_BACK_IN_STOCK'
            """, new NpgsqlParameter("customerId", data.CustomerId)));
    }

    [Fact]
    public async Task Price_transitions_ignore_replay_and_increase_but_emit_each_new_low()
    {
        await ClearQueueAsync();
        var data = await SeedSubscriptionAsync(emailVerified: true, emailEnabled: false);
        await ExecuteAsync("UPDATE store.product_skus SET regular_price=2100000 WHERE id=@skuId",
            new NpgsqlParameter("skuId", data.SkuId));
        Assert.Equal(1, await CreateWorker().ProcessBatchAsync());
        Assert.Equal(0, await ScalarAsync<int>("""
            SELECT count(*)::int FROM store.in_app_notifications
            WHERE recipient_id=@customerId AND notification_type='WISHLIST_PRICE_DROP'
            """, new NpgsqlParameter("customerId", data.CustomerId)));

        await ExecuteAsync("UPDATE store.product_skus SET regular_price=1800000 WHERE id=@skuId",
            new NpgsqlParameter("skuId", data.SkuId));
        Assert.Equal(1, await CreateWorker().ProcessBatchAsync());
        Assert.Equal(0, await CreateWorker().ProcessBatchAsync());
        await ExecuteAsync("UPDATE store.product_skus SET regular_price=1700000 WHERE id=@skuId",
            new NpgsqlParameter("skuId", data.SkuId));
        Assert.Equal(1, await CreateWorker().ProcessBatchAsync());
        Assert.Equal(2, await ScalarAsync<int>("""
            SELECT count(*)::int FROM store.in_app_notifications
            WHERE recipient_id=@customerId AND notification_type='WISHLIST_PRICE_DROP'
            """, new NpgsqlParameter("customerId", data.CustomerId)));
    }

    [Fact]
    public async Task Suspend_updates_private_baseline_and_resume_emits_only_back_in_stock()
    {
        await ClearQueueAsync();
        var data = await SeedSubscriptionAsync(emailVerified: true, emailEnabled: false);
        await ExecuteAsync("""
            UPDATE store.inventory_balances SET physical_qty=1, available_qty=1 WHERE product_sku_id=@skuId;
            DELETE FROM store.product_alert_refresh_queue WHERE product_id=@productId;
            UPDATE store.customer_wishlist_items
            SET last_observed_in_stock=true, last_observed_min_price=2000000
            WHERE customer_id=@customerId AND product_id=@productId;
            UPDATE store.products SET publication_status='SUSPENDED' WHERE id=@productId;
            """, new NpgsqlParameter("skuId", data.SkuId), new NpgsqlParameter("productId", data.ProductId),
            new NpgsqlParameter("customerId", data.CustomerId));
        Assert.Equal(1, await CreateWorker().ProcessBatchAsync());
        Assert.True(await ScalarAsync<bool>("""
            SELECT last_observed_in_stock=false AND last_observed_min_price IS NULL
            FROM store.customer_wishlist_items
            WHERE customer_id=@customerId AND product_id=@productId
            """, new NpgsqlParameter("customerId", data.CustomerId), new NpgsqlParameter("productId", data.ProductId)));
        Assert.Equal(0, await ScalarAsync<int>("""
            SELECT count(*)::int FROM store.in_app_notifications
            WHERE recipient_id=@customerId AND notification_type LIKE 'WISHLIST_%'
            """, new NpgsqlParameter("customerId", data.CustomerId)));

        await ExecuteAsync("UPDATE store.products SET publication_status='PUBLISHED' WHERE id=@productId",
            new NpgsqlParameter("productId", data.ProductId));
        Assert.Equal(1, await CreateWorker().ProcessBatchAsync());
        Assert.Equal(1, await ScalarAsync<int>("""
            SELECT count(*)::int FROM store.in_app_notifications
            WHERE recipient_id=@customerId AND notification_type='WISHLIST_BACK_IN_STOCK'
            """, new NpgsqlParameter("customerId", data.CustomerId)));
        Assert.Equal(0, await ScalarAsync<int>("""
            SELECT count(*)::int FROM store.in_app_notifications
            WHERE recipient_id=@customerId AND notification_type='WISHLIST_PRICE_DROP'
            """, new NpgsqlParameter("customerId", data.CustomerId)));
    }

    [Fact]
    public async Task Wishlist_removed_after_snapshot_is_revalidated_without_notification()
    {
        await ClearQueueAsync();
        var data = await SeedSubscriptionAsync(emailVerified: true, emailEnabled: true);
        await ExecuteAsync("UPDATE store.inventory_balances SET physical_qty=1, available_qty=1 WHERE product_sku_id=@skuId",
            new NpgsqlParameter("skuId", data.SkuId));

        Assert.Equal(1, await CreateWorker(new DeleteWishlistCheckpoint(fixture, data)).ProcessBatchAsync());
        Assert.Equal(0, await ScalarAsync<int>("""
            SELECT count(*)::int FROM store.in_app_notifications
            WHERE recipient_id=@customerId AND notification_type LIKE 'WISHLIST_%'
            """, new NpgsqlParameter("customerId", data.CustomerId)));
        Assert.Equal(0, await ScalarAsync<int>("""
            SELECT count(*)::int FROM store.commerce_email_outbox WHERE recipient_id=@customerId
            """, new NpgsqlParameter("customerId", data.CustomerId)));
    }

    [Fact]
    public async Task Cancellation_after_claim_releases_lease_and_retry_is_exactly_once()
    {
        await ClearQueueAsync();
        var data = await SeedSubscriptionAsync(emailVerified: true, emailEnabled: false);
        await ExecuteAsync("UPDATE store.inventory_balances SET physical_qty=1, available_qty=1 WHERE product_sku_id=@skuId",
            new NpgsqlParameter("skuId", data.SkuId));
        var checkpoint = new CancellationCheckpoint();
        using var cancellation = new CancellationTokenSource();
        var processing = CreateWorker(checkpoint).ProcessBatchAsync(cancellation.Token);
        await checkpoint.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        Assert.True(await ScalarAsync<bool>("""
            SELECT locked_at IS NULL AND locked_by IS NULL
            FROM store.product_alert_refresh_queue WHERE product_id=@productId
            """, new NpgsqlParameter("productId", data.ProductId)));
        Assert.Equal(1, await CreateWorker().ProcessBatchAsync());
        Assert.Equal(1, await ScalarAsync<int>("""
            SELECT count(*)::int FROM store.in_app_notifications
            WHERE recipient_id=@customerId AND notification_type='WISHLIST_BACK_IN_STOCK'
            """, new NpgsqlParameter("customerId", data.CustomerId)));
    }

    [Fact]
    public async Task Reservation_to_zero_updates_baseline_without_alert_then_release_emits_once()
    {
        await ClearQueueAsync();
        var data = await SeedSubscriptionAsync(emailVerified: true, emailEnabled: false);
        await ExecuteAsync("""
            UPDATE store.inventory_balances
            SET physical_qty=1, available_qty=1, reserved_qty=0 WHERE product_sku_id=@skuId;
            UPDATE store.customer_wishlist_items SET last_observed_in_stock=true
            WHERE customer_id=@customerId AND product_id=@productId;
            DELETE FROM store.product_alert_refresh_queue WHERE product_id=@productId;
            UPDATE store.inventory_balances
            SET available_qty=0, reserved_qty=1 WHERE product_sku_id=@skuId;
            """, new NpgsqlParameter("skuId", data.SkuId), new NpgsqlParameter("customerId", data.CustomerId),
            new NpgsqlParameter("productId", data.ProductId));
        Assert.Equal(1, await CreateWorker().ProcessBatchAsync());
        Assert.Equal(0, await ScalarAsync<int>("""
            SELECT count(*)::int FROM store.in_app_notifications
            WHERE recipient_id=@customerId AND notification_type='WISHLIST_BACK_IN_STOCK'
            """, new NpgsqlParameter("customerId", data.CustomerId)));
        Assert.False(await ScalarAsync<bool>("""
            SELECT last_observed_in_stock FROM store.customer_wishlist_items
            WHERE customer_id=@customerId AND product_id=@productId
            """, new NpgsqlParameter("customerId", data.CustomerId), new NpgsqlParameter("productId", data.ProductId)));

        await ExecuteAsync("""
            UPDATE store.inventory_balances
            SET available_qty=1, reserved_qty=0 WHERE product_sku_id=@skuId
            """, new NpgsqlParameter("skuId", data.SkuId));
        Assert.Equal(1, await CreateWorker().ProcessBatchAsync());
        Assert.Equal(1, await ScalarAsync<int>("""
            SELECT count(*)::int FROM store.in_app_notifications
            WHERE recipient_id=@customerId AND notification_type='WISHLIST_BACK_IN_STOCK'
            """, new NpgsqlParameter("customerId", data.CustomerId)));
    }

    [Fact]
    public async Task Flash_sale_price_changes_do_not_enqueue_regular_price_alerts()
    {
        await ClearQueueAsync();
        var data = await SeedSubscriptionAsync(emailVerified: true, emailEnabled: false);
        var campaignId = Guid.NewGuid();
        var offerId = Guid.NewGuid();
        await ExecuteAsync("""
            INSERT INTO store.flash_sale_campaigns(
                id, name, starts_at, ends_at, campaign_status, created_by)
            VALUES(@campaignId, 'Alert isolation campaign', now()-interval '1 minute',
                   now()+interval '1 hour', 'ACTIVE', @customerId);
            INSERT INTO store.flash_sale_offers(
                id, campaign_id, product_sku_id, sale_price, quota_total, per_customer_limit)
            VALUES(@offerId, @campaignId, @skuId, 1500000, 10, 1);
            UPDATE store.flash_sale_offers SET sale_price=1400000 WHERE id=@offerId;
            """, new NpgsqlParameter("campaignId", campaignId), new NpgsqlParameter("offerId", offerId),
            new NpgsqlParameter("customerId", data.CustomerId), new NpgsqlParameter("skuId", data.SkuId));

        Assert.Equal(0, await CreateWorker().ProcessBatchAsync());
        Assert.False(await ScalarAsync<bool>("""
            SELECT EXISTS(SELECT 1 FROM store.product_alert_refresh_queue WHERE product_id=@productId)
            """, new NpgsqlParameter("productId", data.ProductId)));
    }

    [Fact]
    public async Task Disabling_before_evaluation_suppresses_event_and_reenable_resets_baseline()
    {
        await ClearQueueAsync();
        var data = await SeedSubscriptionAsync(emailVerified: true, emailEnabled: false);
        await ExecuteAsync("UPDATE store.inventory_balances SET physical_qty=1, available_qty=1 WHERE product_sku_id=@skuId",
            new NpgsqlParameter("skuId", data.SkuId));
        using (var scope = fixture.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<HighConcurrencyCommerce.Application.Wishlist.IWishlistAlertService>();
            var disabled = await service.UpdateSettingsAsync(data.CustomerId, data.ProductId,
                new HighConcurrencyCommerce.Application.Wishlist.UpdateWishlistAlertSettingsRequest
                {
                    BackInStockEnabled = false,
                    PriceDropEnabled = false,
                    EmailEnabled = false,
                    ExpectedRevision = 1
                });
            Assert.Equal(2, disabled.Revision);
        }
        Assert.Equal(1, await CreateWorker().ProcessBatchAsync());
        Assert.Equal(0, await ScalarAsync<int>("""
            SELECT count(*)::int FROM store.in_app_notifications
            WHERE recipient_id=@customerId AND notification_type LIKE 'WISHLIST_%'
            """, new NpgsqlParameter("customerId", data.CustomerId)));

        using (var scope = fixture.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<HighConcurrencyCommerce.Application.Wishlist.IWishlistAlertService>();
            var enabled = await service.UpdateSettingsAsync(data.CustomerId, data.ProductId,
                new HighConcurrencyCommerce.Application.Wishlist.UpdateWishlistAlertSettingsRequest
                {
                    BackInStockEnabled = true,
                    PriceDropEnabled = false,
                    EmailEnabled = false,
                    ExpectedRevision = 2
                });
            Assert.Equal(3, enabled.Revision);
        }
        Assert.True(await ScalarAsync<bool>("""
            SELECT last_observed_in_stock=true AND alert_sequence=0
            FROM store.customer_wishlist_items
            WHERE customer_id=@customerId AND product_id=@productId
            """, new NpgsqlParameter("customerId", data.CustomerId), new NpgsqlParameter("productId", data.ProductId)));
        Assert.Equal(0, await CreateWorker().ProcessBatchAsync());
    }

    private ProductAlertEvaluationWorker CreateWorker(IProductAlertEvaluationCheckpoint? checkpoint = null) => new(
        fixture.Services.GetRequiredService<IServiceScopeFactory>(),
        TimeProvider.System,
        checkpoint ?? new NoOpProductAlertEvaluationCheckpoint(),
        fixture.Services.GetRequiredService<ILogger<ProductAlertEvaluationWorker>>());

    private async Task<SeedData> SeedSubscriptionAsync(bool emailVerified, bool emailEnabled)
    {
        var customerId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var skuId = Guid.NewGuid();
        await ExecuteAsync("""
            INSERT INTO store.user_accounts(
                id, role_id, email, password_hash, full_name, account_status, email_verified_at)
            VALUES(@customerId, (SELECT id FROM store.roles WHERE role_key='CUSTOMER'),
                   @email, 'x', 'Alert Worker Customer', 'ACTIVE',
                   CASE WHEN @verified THEN now() ELSE NULL END);
            INSERT INTO store.products(id, title, publication_status, created_by, published_by, published_at)
            VALUES(@productId, @title, 'PUBLISHED', @customerId, @customerId, now());
            INSERT INTO store.product_skus(id, product_id, sku_code, option_label, regular_price, sku_status)
            VALUES(@skuId, @productId, @skuCode, '4U/G5', 2000000, 'ACTIVE');
            INSERT INTO store.inventory_balances(
                id, product_sku_id, physical_qty, available_qty, reserved_qty, allocated_qty)
            VALUES(gen_random_uuid(), @skuId, 0, 0, 0, 0);
            INSERT INTO store.customer_wishlist_items(
                customer_id, product_id, back_in_stock_enabled, price_drop_enabled,
                email_enabled, last_observed_in_stock, last_observed_min_price,
                alert_revision, last_evaluated_at)
            VALUES(@customerId, @productId, true, true, @emailEnabled, false, 2000000, 1, now());
            DELETE FROM store.product_alert_refresh_queue WHERE product_id=@productId;
            """,
            new NpgsqlParameter("customerId", customerId),
            new NpgsqlParameter("productId", productId),
            new NpgsqlParameter("skuId", skuId),
            new NpgsqlParameter("email", $"alert-worker-{customerId:N}@example.invalid"),
            new NpgsqlParameter("verified", emailVerified),
            new NpgsqlParameter("emailEnabled", emailEnabled),
            new NpgsqlParameter("title", $"Alert Worker Product {productId:N}"),
            new NpgsqlParameter("skuCode", $"ALERT-WORKER-{skuId:N}"));
        return new SeedData(customerId, productId, skuId);
    }

    private async Task ExecuteAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(sql, db);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private Task ClearQueueAsync() => ExecuteAsync("DELETE FROM store.product_alert_refresh_queue");

    private async Task<T> ScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(sql, db);
        command.Parameters.AddRange(parameters);
        return (T)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Expected scalar."));
    }

    private sealed record SeedData(Guid CustomerId, Guid ProductId, Guid SkuId);

    private sealed class MutatingCheckpoint(CatalogFixture fixture, Guid productId, Guid skuId)
        : IProductAlertEvaluationCheckpoint
    {
        private int invoked;

        public async Task AfterSnapshotAsync(Guid currentProductId, long eventVersion, CancellationToken ct)
        {
            if (currentProductId != productId || Interlocked.Exchange(ref invoked, 1) != 0) return;
            await using var db = await fixture.OpenDatabaseAsync();
            await using var command = new NpgsqlCommand(
                "UPDATE store.product_skus SET regular_price=1900000 WHERE id=@skuId", db);
            command.Parameters.AddWithValue("skuId", skuId);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private sealed class ThrowingCheckpoint : IProductAlertEvaluationCheckpoint
    {
        public Task AfterSnapshotAsync(Guid productId, long eventVersion, CancellationToken ct) =>
            throw new InvalidOperationException("sensitive infrastructure detail must not be persisted");
    }

    private sealed class DeleteWishlistCheckpoint(CatalogFixture fixture, SeedData data)
        : IProductAlertEvaluationCheckpoint
    {
        public async Task AfterSnapshotAsync(Guid productId, long eventVersion, CancellationToken ct)
        {
            await using var db = await fixture.OpenDatabaseAsync();
            await using var command = new NpgsqlCommand("""
                DELETE FROM store.customer_wishlist_items
                WHERE customer_id=@customerId AND product_id=@productId
                """, db);
            command.Parameters.AddWithValue("customerId", data.CustomerId);
            command.Parameters.AddWithValue("productId", data.ProductId);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private sealed class CancellationCheckpoint : IProductAlertEvaluationCheckpoint
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task AfterSnapshotAsync(Guid productId, long eventVersion, CancellationToken ct)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }
}
