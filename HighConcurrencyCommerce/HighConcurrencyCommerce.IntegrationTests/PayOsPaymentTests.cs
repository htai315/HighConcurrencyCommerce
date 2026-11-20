using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HighConcurrencyCommerce.Application.Fulfillment;
using HighConcurrencyCommerce.Application.Payments;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class PayOsPaymentTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "A secure PayOS pass 123!";

    [Fact]
    public async Task Provider_capability_is_public_minimal_and_tracks_feature_flag()
    {
        var gateway = new FakePayOsGateway();
        await using var enabledApp = fixture.CreatePayOsApplication(gateway);
        using var enabledClient = enabledApp.CreateClient(ClientOptions());
        using var enabledResponse = await enabledClient.GetAsync("/api/v1/payments/providers");
        Assert.Equal(HttpStatusCode.OK, enabledResponse.StatusCode);
        var enabledBody = await enabledResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(enabledBody.GetProperty("payOs").GetProperty("enabled").GetBoolean());
        Assert.DoesNotContain("key", enabledBody.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", enabledBody.ToString(), StringComparison.OrdinalIgnoreCase);

        await using var disabledApp = fixture.CreatePayOsApplication(gateway, enabled: false);
        using var disabledClient = disabledApp.CreateClient(ClientOptions());
        var disabledBody = await disabledClient.GetFromJsonAsync<JsonElement>("/api/v1/payments/providers");
        Assert.False(disabledBody.GetProperty("payOs").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Pending_PayOs_order_is_customer_cancellable_while_inventory_is_held()
    {
        var gateway = new FakePayOsGateway();
        await using var app = fixture.CreatePayOsApplication(gateway);
        using var client = app.CreateClient(ClientOptions());
        var customerId = await RegisterAndLogin(client);
        var (orderId, _) = await CreatePendingOrderWithReservation(customerId);

        var order = await client.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{orderId}");
        Assert.Equal("PENDING_PAYMENT", order.GetProperty("orderStatus").GetString());
        Assert.Equal("PENDING", order.GetProperty("paymentStatus").GetString());
        Assert.Equal("HELD", order.GetProperty("reservationStatus").GetString());
        Assert.True(order.GetProperty("canCancel").GetBoolean());
    }

    [Fact]
    public async Task Create_attempt_persists_link_and_replay_does_not_call_provider_twice()
    {
        var gateway = new FakePayOsGateway();
        await using var app = fixture.CreatePayOsApplication(gateway);
        using var client = app.CreateClient(ClientOptions());
        var customerId = await RegisterAndLogin(client);
        var orderId = await CreatePendingOrder(customerId);

        using var first = await Send(client, HttpMethod.Post,
            $"customer/orders/{orderId}/payment/payos-attempts");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("LINK_READY", firstBody.GetProperty("status").GetString());
        Assert.Equal("pay.payos.vn", new Uri(firstBody.GetProperty("checkoutUrl").GetString()!).Host);

        using var replay = await Send(client, HttpMethod.Post,
            $"customer/orders/{orderId}/payment/payos-attempts");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(1, gateway.CreateCount);

        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            SELECT count(*)::int, min(attempt_status), min(provider_payment_link_id)
            FROM store.payment_attempts pa
            JOIN store.payments p ON p.id=pa.payment_id
            WHERE p.order_id=@order
            GROUP BY p.order_id
            """, db);
        command.Parameters.AddWithValue("order", orderId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("LINK_READY", reader.GetString(1));
        Assert.StartsWith("test-link-", reader.GetString(2), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ambiguous_create_is_not_failed_and_retry_queries_instead_of_recreating()
    {
        var gateway = new FakePayOsGateway { CreateFailure = new PayOsGatewayFailure("TIMEOUT", "timeout", true) };
        await using var app = fixture.CreatePayOsApplication(gateway);
        using var client = app.CreateClient(ClientOptions());
        var customerId = await RegisterAndLogin(client);
        var orderId = await CreatePendingOrder(customerId);

        using var first = await Send(client, HttpMethod.Post,
            $"customer/orders/{orderId}/payment/payos-attempts");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal("PAYMENT_STATE_UNRESOLVED", (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        gateway.CreateFailure = null;
        await using (var ageDb = await fixture.OpenDatabaseAsync())
        await using (var age = new NpgsqlCommand("""
            UPDATE store.payment_attempts pa
            SET created_at=now()-interval '1 minute'
            FROM store.payments p
            WHERE p.id=pa.payment_id AND p.order_id=@order
            """, ageDb))
        {
            age.Parameters.AddWithValue("order", orderId);
            await age.ExecuteNonQueryAsync();
        }
        using var retry = await Send(client, HttpMethod.Post,
            $"customer/orders/{orderId}/payment/payos-attempts");
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        Assert.Equal(1, gateway.CreateCount);
        Assert.Equal(1, gateway.GetCount);

        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            SELECT attempt_status, last_error_code IS NOT NULL
            FROM store.payment_attempts pa
            JOIN store.payments p ON p.id=pa.payment_id
            WHERE p.order_id=@order
            """, db);
        command.Parameters.AddWithValue("order", orderId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("CREATED", reader.GetString(0));
        Assert.False(reader.GetBoolean(1));
    }

    [Fact]
    public async Task Concurrent_attempt_requests_create_only_one_provider_link()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new FakePayOsGateway { CreateEntered = entered, ReleaseCreate = release };
        await using var app = fixture.CreatePayOsApplication(gateway);
        using var firstClient = app.CreateClient(ClientOptions());
        var customerId = await RegisterAndLogin(firstClient);
        var orderId = await CreatePendingOrder(customerId);
        using var secondClient = app.CreateClient(ClientOptions());
        secondClient.DefaultRequestHeaders.Add("Cookie", firstClient.DefaultRequestHeaders.GetValues("Cookie").Single());

        var firstTask = Send(firstClient, HttpMethod.Post, $"customer/orders/{orderId}/payment/payos-attempts");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var secondTask = Send(secondClient, HttpMethod.Post, $"customer/orders/{orderId}/payment/payos-attempts");
        await Task.Delay(100);
        release.SetResult();

        using var first = await firstTask;
        using var second = await secondTask;
        var allowed = new[] { HttpStatusCode.Created, HttpStatusCode.Conflict, HttpStatusCode.OK };
        Assert.True(allowed.Contains(first.StatusCode),
            $"First response: {(int)first.StatusCode} {await first.Content.ReadAsStringAsync()}");
        Assert.True(allowed.Contains(second.StatusCode),
            $"Second response: {(int)second.StatusCode} {await second.Content.ReadAsStringAsync()}");
        Assert.Equal(1, gateway.CreateCount);
        Assert.Equal(0, gateway.GetCount);
    }

    [Fact]
    public async Task Payment_status_is_owner_only_and_does_not_require_provider_credentials_when_disabled()
    {
        var gateway = new FakePayOsGateway();
        await using var enabledApp = fixture.CreatePayOsApplication(gateway);
        using var owner = enabledApp.CreateClient(ClientOptions());
        var ownerId = await RegisterAndLogin(owner);
        var orderId = await CreatePendingOrder(ownerId);
        using var other = enabledApp.CreateClient(ClientOptions());
        await RegisterAndLogin(other);

        using var forbidden = await other.GetAsync($"/api/v1/customer/orders/{orderId}/payment");
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);

        await using var disabledApp = fixture.CreatePayOsApplication(gateway, enabled: false);
        using var disabledClient = disabledApp.CreateClient(ClientOptions());
        await LoginExisting(disabledClient, await GetEmail(ownerId));
        using var status = await disabledClient.GetAsync($"/api/v1/customer/orders/{orderId}/payment");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Contains("no-store", status.Headers.CacheControl?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Webhook_is_durable_sanitized_and_duplicate_safe_without_csrf()
    {
        var gateway = new FakePayOsGateway();
        await using var app = fixture.CreatePayOsApplication(gateway);
        using var client = app.CreateClient(ClientOptions());
        var payload = WebhookPayload(new string('a', 64));

        using var first = await client.PostAsJsonAsync("/api/v1/integrations/payos/webhook", payload);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var duplicate = await client.PostAsJsonAsync("/api/v1/integrations/payos/webhook", payload);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.True((await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("duplicate").GetBoolean());
        Assert.Equal(1, gateway.VerifyCount);

        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            SELECT count(*)::int, bool_and(signature_valid), min(process_result), min(payload::text)
            FROM store.provider_callback_events
            WHERE provider_order_code=987654321 AND signature_valid
            """, db);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Equal("REJECTED", reader.GetString(2));
        var stored = reader.GetString(3);
        Assert.DoesNotContain("sensitive-account", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("counter-account", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verified_webhook_atomically_confirms_order_allocates_stock_and_is_idempotent()
    {
        var gateway = new FakePayOsGateway();
        await using var app = fixture.CreatePayOsApplication(gateway);
        using var client = app.CreateClient(ClientOptions());
        var customerId = await RegisterAndLogin(client);
        var (orderId, skuId) = await CreatePendingOrderWithReservation(customerId);

        using (var attempt = await Send(client, HttpMethod.Post,
                   $"customer/orders/{orderId}/payment/payos-attempts"))
            Assert.Equal(HttpStatusCode.Created, attempt.StatusCode);

        var orderCode = await ProviderOrderCode(orderId);
        var payload = WebhookPayload(new string('d', 64), orderCode, $"paid-{Guid.NewGuid():N}");
        using var first = await client.PostAsJsonAsync("/api/v1/integrations/payos/webhook", payload);
        Assert.True(first.StatusCode == HttpStatusCode.OK,
            $"Webhook response: {(int)first.StatusCode} {await first.Content.ReadAsStringAsync()}");
        using var replay = await client.PostAsJsonAsync("/api/v1/integrations/payos/webhook", payload);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            SELECT o.order_status,p.payment_status,r.reservation_status,
                   b.reserved_qty,b.allocated_qty,
                   (SELECT count(*) FROM store.payment_transactions t WHERE t.payment_id=p.id),
                   (SELECT process_result FROM store.provider_callback_events c WHERE c.provider_order_code=@code LIMIT 1),
                   (SELECT count(*) FROM store.inventory_movements m WHERE m.source_id=o.id AND m.movement_type='ALLOCATE')
            FROM store.sales_orders o
            JOIN store.payments p ON p.order_id=o.id
            JOIN store.stock_reservations r ON r.order_id=o.id
            JOIN store.inventory_balances b ON b.product_sku_id=@sku
            WHERE o.id=@order
            """, db);
        command.Parameters.AddWithValue("order", orderId);
        command.Parameters.AddWithValue("sku", skuId);
        command.Parameters.AddWithValue("code", orderCode);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("CONFIRMED", reader.GetString(0));
        Assert.Equal("SUCCESS", reader.GetString(1));
        Assert.Equal("ALLOCATED", reader.GetString(2));
        Assert.Equal(0, reader.GetInt32(3));
        Assert.Equal(2, reader.GetInt32(4));
        Assert.Equal(1L, reader.GetInt64(5));
        Assert.Equal("APPLIED", reader.GetString(6));
        Assert.Equal(1L, reader.GetInt64(7));
    }

    [Fact]
    public async Task Second_paid_reference_opens_duplicate_collection_case_without_changing_successful_payment()
    {
        var gateway = new FakePayOsGateway();
        await using var app = fixture.CreatePayOsApplication(gateway);
        using var client = app.CreateClient(ClientOptions());
        var customerId = await RegisterAndLogin(client);
        var (orderId, _) = await CreatePendingOrderWithReservation(customerId);
        using (var attempt = await Send(client, HttpMethod.Post,
                   $"customer/orders/{orderId}/payment/payos-attempts"))
            Assert.Equal(HttpStatusCode.Created, attempt.StatusCode);
        var orderCode = await ProviderOrderCode(orderId);

        using var first = await client.PostAsJsonAsync("/api/v1/integrations/payos/webhook",
            WebhookPayload(new string('7', 64), orderCode, $"paid-first-{Guid.NewGuid():N}"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var second = await client.PostAsJsonAsync("/api/v1/integrations/payos/webhook",
            WebhookPayload(new string('8', 64), orderCode, $"paid-second-{Guid.NewGuid():N}"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            SELECT o.order_status,p.payment_status,
                   (SELECT count(*) FROM store.payment_transactions t WHERE t.payment_id=p.id),
                   (SELECT count(*) FROM store.payment_reconciliation_cases c
                    WHERE c.payment_id=p.id AND c.reason='DUPLICATE_COLLECTION'
                      AND c.transaction_id IS NOT NULL),
                   (SELECT count(*) FROM store.provider_callback_events c
                    WHERE c.provider_order_code=@code AND c.process_result='ERROR')
            FROM store.sales_orders o JOIN store.payments p ON p.order_id=o.id
            WHERE o.id=@order
            """, db);
        command.Parameters.AddWithValue("order", orderId);
        command.Parameters.AddWithValue("code", orderCode);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("CONFIRMED", reader.GetString(0));
        Assert.Equal("SUCCESS", reader.GetString(1));
        Assert.Equal(2L, reader.GetInt64(2));
        Assert.Equal(1L, reader.GetInt64(3));
        Assert.Equal(1L, reader.GetInt64(4));
    }

    [Fact]
    public async Task Late_paid_webhook_opens_reconciliation_without_resurrecting_order_or_stock()
    {
        var gateway = new FakePayOsGateway();
        await using var app = fixture.CreatePayOsApplication(gateway);
        using var client = app.CreateClient(ClientOptions());
        var customerId = await RegisterAndLogin(client);
        var (orderId, skuId) = await CreatePendingOrderWithReservation(customerId);
        using (var attempt = await Send(client, HttpMethod.Post,
                   $"customer/orders/{orderId}/payment/payos-attempts"))
            Assert.Equal(HttpStatusCode.Created, attempt.StatusCode);
        var orderCode = await ProviderOrderCode(orderId);

        await using (var db = await fixture.OpenDatabaseAsync())
        await using (var close = new NpgsqlCommand("""
            UPDATE store.sales_orders SET order_status='EXPIRED',closed_reason='PAYMENT_EXPIRED' WHERE id=@order;
            UPDATE store.payments SET payment_status='VOID' WHERE order_id=@order;
            UPDATE store.stock_reservations SET reservation_status='RELEASED' WHERE order_id=@order;
            UPDATE store.stock_reservation_lines SET line_status='RELEASED'
            WHERE reservation_id=(SELECT id FROM store.stock_reservations WHERE order_id=@order);
            UPDATE store.inventory_balances SET available_qty=10,reserved_qty=0 WHERE product_sku_id=@sku;
            """, db))
        {
            close.Parameters.AddWithValue("order", orderId);
            close.Parameters.AddWithValue("sku", skuId);
            await close.ExecuteNonQueryAsync();
        }

        var payload = WebhookPayload(new string('e', 64), orderCode, $"late-{Guid.NewGuid():N}");
        using var response = await client.PostAsJsonAsync("/api/v1/integrations/payos/webhook", payload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var verifyDb = await fixture.OpenDatabaseAsync();
        await using var verify = new NpgsqlCommand("""
            SELECT o.order_status,p.payment_status,r.reservation_status,b.available_qty,b.reserved_qty,b.allocated_qty,
                   (SELECT count(*) FROM store.payment_reconciliation_cases c WHERE c.payment_id=p.id AND c.reason='LATE_PAID' AND c.case_status='OPEN'),
                   (SELECT transaction_status FROM store.payment_transactions t WHERE t.payment_id=p.id LIMIT 1),
                   (SELECT process_result FROM store.provider_callback_events c WHERE c.provider_order_code=@code LIMIT 1)
            FROM store.sales_orders o JOIN store.payments p ON p.order_id=o.id
            JOIN store.stock_reservations r ON r.order_id=o.id
            JOIN store.inventory_balances b ON b.product_sku_id=@sku
            WHERE o.id=@order
            """, verifyDb);
        verify.Parameters.AddWithValue("order", orderId);
        verify.Parameters.AddWithValue("sku", skuId);
        verify.Parameters.AddWithValue("code", orderCode);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("EXPIRED", reader.GetString(0));
        Assert.Equal("LATE_PAID", reader.GetString(1));
        Assert.Equal("RELEASED", reader.GetString(2));
        Assert.Equal(10, reader.GetInt32(3));
        Assert.Equal(0, reader.GetInt32(4));
        Assert.Equal(0, reader.GetInt32(5));
        Assert.Equal(1L, reader.GetInt64(6));
        Assert.Equal("RECONCILIATION_REQUIRED", reader.GetString(7));
        Assert.Equal("LATE_PAID", reader.GetString(8));
    }

    [Fact]
    public async Task Amount_mismatch_never_confirms_order_and_opens_reconciliation()
    {
        var gateway = new FakePayOsGateway();
        await using var app = fixture.CreatePayOsApplication(gateway);
        using var client = app.CreateClient(ClientOptions());
        var customerId = await RegisterAndLogin(client);
        var (orderId, skuId) = await CreatePendingOrderWithReservation(customerId);
        using (var attempt = await Send(client, HttpMethod.Post,
                   $"customer/orders/{orderId}/payment/payos-attempts"))
            Assert.Equal(HttpStatusCode.Created, attempt.StatusCode);
        var orderCode = await ProviderOrderCode(orderId);

        var payload = WebhookPayload(new string('f', 64), orderCode,
            $"mismatch-{Guid.NewGuid():N}", amount: 1999);
        using var response = await client.PostAsJsonAsync("/api/v1/integrations/payos/webhook", payload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            SELECT o.order_status,p.payment_status,b.reserved_qty,b.allocated_qty,
                   (SELECT count(*) FROM store.payment_reconciliation_cases c WHERE c.payment_id=p.id AND c.reason='AMOUNT_MISMATCH'),
                   (SELECT transaction_status FROM store.payment_transactions t WHERE t.payment_id=p.id LIMIT 1)
            FROM store.sales_orders o JOIN store.payments p ON p.order_id=o.id
            JOIN store.inventory_balances b ON b.product_sku_id=@sku WHERE o.id=@order
            """, db);
        command.Parameters.AddWithValue("order", orderId);
        command.Parameters.AddWithValue("sku", skuId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("PENDING_PAYMENT", reader.GetString(0));
        Assert.Equal("PENDING", reader.GetString(1));
        Assert.Equal(2, reader.GetInt32(2));
        Assert.Equal(0, reader.GetInt32(3));
        Assert.Equal(1L, reader.GetInt64(4));
        Assert.Equal("RECONCILIATION_REQUIRED", reader.GetString(5));
    }

    [Fact]
    public async Task Customer_cancel_calls_provider_outside_transaction_and_releases_hold_exactly_once()
    {
        var gateway = new FakePayOsGateway();
        await using var app = fixture.CreatePayOsApplication(gateway);
        using var client = app.CreateClient(ClientOptions());
        var customerId = await RegisterAndLogin(client);
        var (orderId, skuId) = await CreatePendingOrderWithReservation(customerId);
        using (var attempt = await Send(client, HttpMethod.Post,
                   $"customer/orders/{orderId}/payment/payos-attempts"))
            Assert.Equal(HttpStatusCode.Created, attempt.StatusCode);

        using var cancel = await Send(client, HttpMethod.Post,
            $"customer/orders/{orderId}/payment/cancel", new { reason = "Khách đổi ý" });
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        Assert.Equal(1, gateway.CancelCount);

        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            SELECT o.order_status,p.payment_status,r.reservation_status,b.available_qty,b.reserved_qty,
                   (SELECT count(*) FROM store.inventory_movements m WHERE m.source_id=o.id AND m.movement_type='RELEASE')
            FROM store.sales_orders o JOIN store.payments p ON p.order_id=o.id
            JOIN store.stock_reservations r ON r.order_id=o.id
            JOIN store.inventory_balances b ON b.product_sku_id=@sku WHERE o.id=@order
            """, db);
        command.Parameters.AddWithValue("order", orderId);
        command.Parameters.AddWithValue("sku", skuId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("CANCELLED", reader.GetString(0));
        Assert.Equal("VOID", reader.GetString(1));
        Assert.Equal("RELEASED", reader.GetString(2));
        Assert.Equal(10, reader.GetInt32(3));
        Assert.Equal(0, reader.GetInt32(4));
        Assert.Equal(1L, reader.GetInt64(5));
    }

    [Fact]
    public async Task Expiry_queries_then_cancels_provider_before_releasing_inventory()
    {
        var gateway = new FakePayOsGateway();
        await using var app = fixture.CreatePayOsApplication(gateway);
        using var client = app.CreateClient(ClientOptions());
        var customerId = await RegisterAndLogin(client);
        var (orderId, skuId) = await CreatePendingOrderWithReservation(customerId);
        using (var attempt = await Send(client, HttpMethod.Post,
                   $"customer/orders/{orderId}/payment/payos-attempts"))
            Assert.Equal(HttpStatusCode.Created, attempt.StatusCode);
        await using (var db = await fixture.OpenDatabaseAsync())
        await using (var due = new NpgsqlCommand(
                         "UPDATE store.sales_orders SET created_at=now()-interval '20 minutes',deadline_at=now()-interval '1 minute' WHERE id=@order", db))
        {
            due.Parameters.AddWithValue("order", orderId);
            await due.ExecuteNonQueryAsync();
        }

        using var scope = app.Services.CreateScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IPayOsLifecycleService>();
        Assert.Equal(1, await lifecycle.ExpireDueAsync());
        Assert.Equal(1, gateway.GetCount);
        Assert.Equal(1, gateway.CancelCount);

        await using var verifyDb = await fixture.OpenDatabaseAsync();
        await using var verify = new NpgsqlCommand("""
            SELECT o.order_status,p.payment_status,b.available_qty,b.reserved_qty
            FROM store.sales_orders o JOIN store.payments p ON p.order_id=o.id
            JOIN store.inventory_balances b ON b.product_sku_id=@sku WHERE o.id=@order
            """, verifyDb);
        verify.Parameters.AddWithValue("order", orderId);
        verify.Parameters.AddWithValue("sku", skuId);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("EXPIRED", reader.GetString(0));
        Assert.Equal("VOID", reader.GetString(1));
        Assert.Equal(10, reader.GetInt32(2));
        Assert.Equal(0, reader.GetInt32(3));
    }

    [Fact]
    public async Task PayOs_paid_order_completes_fulfillment_without_cod_collection()
    {
        var gateway = new FakePayOsGateway();
        await using var app = fixture.CreatePayOsApplication(gateway);
        using var client = app.CreateClient(ClientOptions());
        var customerId = await RegisterAndLogin(client);
        var (orderId, skuId) = await CreatePendingOrderWithReservation(customerId);
        using (var attempt = await Send(client, HttpMethod.Post,
                   $"customer/orders/{orderId}/payment/payos-attempts"))
            Assert.Equal(HttpStatusCode.Created, attempt.StatusCode);
        var orderCode = await ProviderOrderCode(orderId);
        using (var webhook = await client.PostAsJsonAsync("/api/v1/integrations/payos/webhook",
                   WebhookPayload(new string('1', 64), orderCode, $"fulfill-{Guid.NewGuid():N}")))
            Assert.Equal(HttpStatusCode.OK, webhook.StatusCode);

        var staffId = await CreateActor("STAFF");
        using var scope = app.Services.CreateScope();
        var fulfillment = scope.ServiceProvider.GetRequiredService<IFulfillmentService>();
        var started = await fulfillment.StartAsync(staffId, orderId, "payos-fulfillment");
        var shipmentId = Assert.IsType<Guid>(started.ShipmentId);
        await fulfillment.PackAsync(staffId, shipmentId, "payos-pack");
        await fulfillment.HandoverAsync(staffId, shipmentId, "payos-handover");
        var delivered = await fulfillment.RecordDeliveryAsync(staffId, shipmentId,
            new DeliveryResultRequest { Result = "DELIVERED" }, "payos-delivered");
        Assert.Equal("COMPLETED", delivered.OrderStatus);
        Assert.Equal("SUCCESS", delivered.PaymentStatus);

        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            SELECT o.order_status,p.payment_status,b.physical_qty,b.allocated_qty,
                   (SELECT count(*) FROM store.payment_transactions t WHERE t.payment_id=p.id AND t.provider='PAYOS'),
                   (SELECT count(*) FROM store.payment_transactions t WHERE t.payment_id=p.id AND t.provider='COD')
            FROM store.sales_orders o JOIN store.payments p ON p.order_id=o.id
            JOIN store.inventory_balances b ON b.product_sku_id=@sku WHERE o.id=@order
            """, db);
        command.Parameters.AddWithValue("order", orderId);
        command.Parameters.AddWithValue("sku", skuId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("COMPLETED", reader.GetString(0));
        Assert.Equal("SUCCESS", reader.GetString(1));
        Assert.Equal(8, reader.GetInt32(2));
        Assert.Equal(0, reader.GetInt32(3));
        Assert.Equal(1L, reader.GetInt64(4));
        Assert.Equal(0L, reader.GetInt64(5));
    }

    [Fact]
    public async Task Concurrent_paid_webhook_winning_before_cancel_never_releases_inventory()
    {
        var cancelEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new FakePayOsGateway { CancelEntered = cancelEntered, ReleaseCancel = releaseCancel };
        await using var app = fixture.CreatePayOsApplication(gateway);
        using var client = app.CreateClient(ClientOptions());
        var customerId = await RegisterAndLogin(client);
        var (orderId, skuId) = await CreatePendingOrderWithReservation(customerId);
        using (var attempt = await Send(client, HttpMethod.Post,
                   $"customer/orders/{orderId}/payment/payos-attempts"))
            Assert.Equal(HttpStatusCode.Created, attempt.StatusCode);
        var orderCode = await ProviderOrderCode(orderId);

        var cancelTask = Send(client, HttpMethod.Post,
            $"customer/orders/{orderId}/payment/cancel", new { reason = "Race cancel" });
        await cancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var webhook = await client.PostAsJsonAsync("/api/v1/integrations/payos/webhook",
            WebhookPayload(new string('2', 64), orderCode, $"race-paid-{Guid.NewGuid():N}"));
        Assert.Equal(HttpStatusCode.OK, webhook.StatusCode);
        releaseCancel.SetResult();
        using var cancel = await cancelTask;
        Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);

        var state = await ReadPaymentState(orderId, skuId);
        Assert.Equal(("CONFIRMED", "SUCCESS", "ALLOCATED", 8, 2), state);
    }

    [Fact]
    public async Task Concurrent_cancel_winning_before_webhook_records_late_paid_without_resurrection()
    {
        var verifyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseVerify = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new FakePayOsGateway { VerifyEntered = verifyEntered, ReleaseVerify = releaseVerify };
        await using var app = fixture.CreatePayOsApplication(gateway);
        using var client = app.CreateClient(ClientOptions());
        var customerId = await RegisterAndLogin(client);
        var (orderId, skuId) = await CreatePendingOrderWithReservation(customerId);
        using (var attempt = await Send(client, HttpMethod.Post,
                   $"customer/orders/{orderId}/payment/payos-attempts"))
            Assert.Equal(HttpStatusCode.Created, attempt.StatusCode);
        var orderCode = await ProviderOrderCode(orderId);

        var webhookTask = client.PostAsJsonAsync("/api/v1/integrations/payos/webhook",
            WebhookPayload(new string('3', 64), orderCode, $"race-late-{Guid.NewGuid():N}"));
        await verifyEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancel = await Send(client, HttpMethod.Post,
            $"customer/orders/{orderId}/payment/cancel", new { reason = "Cancel wins" });
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        releaseVerify.SetResult();
        using var webhook = await webhookTask;
        Assert.Equal(HttpStatusCode.OK, webhook.StatusCode);

        var state = await ReadPaymentState(orderId, skuId);
        Assert.Equal(("CANCELLED", "LATE_PAID", "RELEASED", 10, 0), state);
    }

    [Fact]
    public async Task Invalid_webhook_signature_is_rejected_and_has_no_business_effect()
    {
        var gateway = new FakePayOsGateway { RejectWebhook = true };
        await using var app = fixture.CreatePayOsApplication(gateway);
        using var client = app.CreateClient(ClientOptions());

        using var response = await client.PostAsJsonAsync(
            "/api/v1/integrations/payos/webhook", WebhookPayload(new string('b', 64)));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("PAYOS_INVALID_SIGNATURE",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            SELECT NOT signature_valid AND process_result='REJECTED'
            FROM store.provider_callback_events
            WHERE provider_order_code=987654321
            ORDER BY received_at DESC LIMIT 1
            """, db);
        Assert.True((bool)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Null_webhook_signature_returns_400_never_500()
    {
        var gateway = new FakePayOsGateway();
        await using var app = fixture.CreatePayOsApplication(gateway);
        using var client = app.CreateClient(ClientOptions());
        var payload = new
        {
            code = "00",
            desc = "success",
            success = true,
            data = new
            {
                orderCode = 987654321L,
                amount = 1000L,
                reference = "null-signature",
                paymentLinkId = "null-signature-link",
                currency = "VND"
            },
            signature = (string?)null
        };

        using var response = await client.PostAsJsonAsync("/api/v1/integrations/payos/webhook", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("PAYOS_WEBHOOK_INVALID",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Signed_but_unsuccessful_webhook_is_audited_without_payment_transition()
    {
        var gateway = new FakePayOsGateway();
        await using var app = fixture.CreatePayOsApplication(gateway);
        using var client = app.CreateClient(ClientOptions());
        const long unsuccessfulOrderCode = 987654399L;
        var payload = WebhookPayload(new string('4', 64), unsuccessfulOrderCode,
            success: false, code: "01");

        using var response = await client.PostAsJsonAsync("/api/v1/integrations/payos/webhook", payload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            SELECT signature_valid,process_result FROM store.provider_callback_events
            WHERE provider_order_code=@code ORDER BY received_at DESC LIMIT 1
            """, db);
        command.Parameters.AddWithValue("code", unsuccessfulOrderCode);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.Equal("REJECTED", reader.GetString(1));
    }

    [Fact]
    public async Task Concurrent_duplicate_webhooks_persist_exactly_one_event_and_never_return_500()
    {
        var gateway = new FakePayOsGateway();
        await using var app = fixture.CreatePayOsApplication(gateway);
        using var client = app.CreateClient(ClientOptions());
        var payload = WebhookPayload(new string('c', 64), 987654322L);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => client.PostAsJsonAsync("/api/v1/integrations/payos/webhook", payload)));
        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }

        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            SELECT count(*)::int FROM store.provider_callback_events
            WHERE provider_order_code=987654322
            """, db);
        Assert.Equal(1, (int)(await command.ExecuteScalarAsync())!);
    }

    private async Task<Guid> RegisterAndLogin(HttpClient client)
    {
        var email = $"payos-{Guid.NewGuid():N}@example.com";
        using var register = await Send(client, HttpMethod.Post, "auth/register", new
        {
            email,
            fullName = "PayOS Test",
            phone = "0901234567",
            password = Password,
            confirmPassword = Password
        });
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        await fixture.VerifyUserAsync(email);
        await LoginExisting(client, email);
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("SELECT id FROM store.user_accounts WHERE email=@email", db);
        command.Parameters.AddWithValue("email", email);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private async Task LoginExisting(HttpClient client, string email)
    {
        using var login = await Send(client, HttpMethod.Post, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-hcc-session="));
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
    }

    private async Task<string> GetEmail(Guid userId)
    {
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("SELECT email FROM store.user_accounts WHERE id=@id", db);
        command.Parameters.AddWithValue("id", userId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<Guid> CreatePendingOrder(Guid customerId)
    {
        var orderId = Guid.NewGuid();
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO store.sales_orders(
                id, customer_id, order_number, payment_method, order_status,
                shipping_address_snapshot, subtotal_amount, voucher_discount_amount,
                shipping_fee, total_amount, deadline_at)
            VALUES(@id,@customer,@number,'PAYOS','PENDING_PAYMENT','{}',2000,0,0,2000,now()+interval '15 minutes');
            INSERT INTO store.payments(id,order_id,payment_method,expected_amount,payment_status)
            VALUES(gen_random_uuid(),@id,'PAYOS',2000,'PENDING');
            """, db);
        command.Parameters.AddWithValue("id", orderId);
        command.Parameters.AddWithValue("customer", customerId);
        command.Parameters.AddWithValue("number", $"HC-PAYOS-{orderId:N}"[..21]);
        await command.ExecuteNonQueryAsync();
        return orderId;
    }

    private async Task<(Guid OrderId, Guid SkuId)> CreatePendingOrderWithReservation(Guid customerId)
    {
        var orderId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var skuId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO store.products(id,title,publication_status,created_by,published_by,published_at)
            VALUES(@product,'PayOS transition product','PUBLISHED',@customer,@customer,now());
            INSERT INTO store.product_skus(id,product_id,sku_code,option_label,regular_price,sku_status)
            VALUES(@sku,@product,@skuCode,'Test',1000,'ACTIVE');
            INSERT INTO store.inventory_balances(id,product_sku_id,physical_qty,available_qty,reserved_qty,allocated_qty,revision)
            VALUES(gen_random_uuid(),@sku,10,8,2,0,0);
            INSERT INTO store.sales_orders(
                id,customer_id,order_number,payment_method,order_status,shipping_address_snapshot,
                subtotal_amount,voucher_discount_amount,shipping_fee,total_amount,deadline_at)
            VALUES(@order,@customer,@number,'PAYOS','PENDING_PAYMENT','{}',2000,0,0,2000,now()+interval '15 minutes');
            INSERT INTO store.sales_order_items(
                id,order_id,product_sku_id,product_title_snapshot,sku_code_snapshot,sku_option_label_snapshot,
                quantity,unit_price)
            VALUES(@item,@order,@sku,'PayOS transition product',@skuCode,'Test',2,1000);
            INSERT INTO store.payments(id,order_id,payment_method,expected_amount,payment_status)
            VALUES(gen_random_uuid(),@order,'PAYOS',2000,'PENDING');
            INSERT INTO store.stock_reservations(id,order_id,reservation_status,reserved_at,expires_at)
            VALUES(@reservation,@order,'HELD',now(),now()+interval '15 minutes');
            INSERT INTO store.stock_reservation_lines(
                id,reservation_id,order_item_id,product_sku_id,quantity,line_status)
            VALUES(gen_random_uuid(),@reservation,@item,@sku,2,'HELD');
            """, db);
        command.Parameters.AddWithValue("order", orderId);
        command.Parameters.AddWithValue("customer", customerId);
        command.Parameters.AddWithValue("product", productId);
        command.Parameters.AddWithValue("sku", skuId);
        command.Parameters.AddWithValue("item", itemId);
        command.Parameters.AddWithValue("reservation", reservationId);
        command.Parameters.AddWithValue("number", $"HC-PAYOS-{orderId:N}"[..21]);
        command.Parameters.AddWithValue("skuCode", "PAY-" + skuId.ToString("N")[..10].ToUpperInvariant());
        await command.ExecuteNonQueryAsync();
        return (orderId, skuId);
    }

    private async Task<long> ProviderOrderCode(Guid orderId)
    {
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            SELECT pa.provider_order_code
            FROM store.payment_attempts pa JOIN store.payments p ON p.id=pa.payment_id
            WHERE p.order_id=@order
            """, db);
        command.Parameters.AddWithValue("order", orderId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<Guid> CreateActor(string roleKey)
    {
        var id = Guid.NewGuid();
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO store.user_accounts(id,role_id,email,full_name,account_status,email_verified_at)
            VALUES(@id,(SELECT id FROM store.roles WHERE role_key=@role),@email,@name,'ACTIVE',now())
            """, db);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("role", roleKey);
        command.Parameters.AddWithValue("email", $"payos-{roleKey.ToLowerInvariant()}-{id:N}@example.invalid");
        command.Parameters.AddWithValue("name", $"PayOS {roleKey}");
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<(string Order, string Payment, string Reservation, int Available, int Allocated)>
        ReadPaymentState(Guid orderId, Guid skuId)
    {
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            SELECT o.order_status,p.payment_status,r.reservation_status,b.available_qty,b.allocated_qty
            FROM store.sales_orders o JOIN store.payments p ON p.order_id=o.id
            JOIN store.stock_reservations r ON r.order_id=o.id
            JOIN store.inventory_balances b ON b.product_sku_id=@sku WHERE o.id=@order
            """, db);
        command.Parameters.AddWithValue("order", orderId);
        command.Parameters.AddWithValue("sku", skuId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4));
    }

    private static WebApplicationFactoryClientOptions ClientOptions() => new()
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false
    };

    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("requestToken").GetString()!;
    }

    private static object WebhookPayload(
        string signature,
        long orderCode = 987654321L,
        string reference = "provider-reference",
        long amount = 2000L,
        bool success = true,
        string code = "00") => new
    {
        code,
        desc = "success",
        success,
        data = new
        {
            orderCode,
            amount,
            description = "HCC TEST",
            accountNumber = "sensitive-account",
            reference,
            transactionDateTime = "2026-11-09 12:00:00",
            currency = "VND",
            paymentLinkId = $"test-link-{orderCode}",
            code,
            desc = "Thành công",
            counterAccountBankId = "bank-id",
            counterAccountBankName = "bank-name",
            counterAccountName = "counter-account",
            counterAccountNumber = "counter-number",
            virtualAccountName = "virtual-name",
            virtualAccountNumber = "virtual-number"
        },
        signature
    };

    private static async Task<HttpResponseMessage> Send(
        HttpClient client,
        HttpMethod method,
        string path,
        object? body = null)
    {
        using var request = new HttpRequestMessage(method, "/api/v1/" + path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (method != HttpMethod.Get) request.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        return await client.SendAsync(request);
    }

    private sealed class FakePayOsGateway : IPayOsGateway
    {
        private int createCount;
        private int getCount;
        private int verifyCount;
        private int cancelCount;
        public int CreateCount => Volatile.Read(ref createCount);
        public int GetCount => Volatile.Read(ref getCount);
        public int VerifyCount => Volatile.Read(ref verifyCount);
        public int CancelCount => Volatile.Read(ref cancelCount);
        public PayOsGatewayFailure? CreateFailure { get; set; }
        public TaskCompletionSource? CreateEntered { get; set; }
        public TaskCompletionSource? ReleaseCreate { get; set; }
        public TaskCompletionSource? CancelEntered { get; set; }
        public TaskCompletionSource? ReleaseCancel { get; set; }
        public TaskCompletionSource? VerifyEntered { get; set; }
        public TaskCompletionSource? ReleaseVerify { get; set; }
        public bool RejectWebhook { get; set; }

        public async Task<PayOsProviderPayment> CreatePaymentLinkAsync(PayOsCreateLinkCommand command, CancellationToken ct = default)
        {
            Interlocked.Increment(ref createCount);
            CreateEntered?.TrySetResult();
            if (ReleaseCreate is not null) await ReleaseCreate.Task.WaitAsync(ct);
            if (CreateFailure is not null) throw CreateFailure;
            return Pending(command.OrderCode, command.Amount,
                $"https://pay.payos.vn/web/test-link-{command.OrderCode}", command.ExpiresAtUnixSeconds);
        }

        public Task<PayOsProviderPayment> GetPaymentAsync(long orderCode, CancellationToken ct = default)
        {
            Interlocked.Increment(ref getCount);
            return Task.FromResult(Pending(orderCode, 2000, null, null));
        }

        public async Task<PayOsProviderPayment> CancelPaymentAsync(long orderCode, string reason, CancellationToken ct = default)
        {
            Interlocked.Increment(ref cancelCount);
            CancelEntered?.TrySetResult();
            if (ReleaseCancel is not null) await ReleaseCancel.Task.WaitAsync(ct);
            return new PayOsProviderPayment(orderCode, 2000, 0, 2000, "CANCELLED",
                $"test-link-{orderCode}", null, null, reason, DateTime.UtcNow);
        }

        public async Task<PayOsVerifiedWebhook> VerifyWebhookAsync(PayOsWebhookEnvelope webhook, CancellationToken ct = default)
        {
            Interlocked.Increment(ref verifyCount);
            if (RejectWebhook)
                throw new PayOsGatewayFailure("PAYOS_INVALID_SIGNATURE", "invalid", false);
            VerifyEntered?.TrySetResult();
            if (ReleaseVerify is not null) await ReleaseVerify.Task.WaitAsync(ct);
            return new PayOsVerifiedWebhook(
                webhook.Data.OrderCode,
                webhook.Data.Amount,
                webhook.Data.Reference,
                webhook.Data.TransactionDateTime,
                webhook.Data.Currency,
                webhook.Data.PaymentLinkId,
                webhook.Data.Code,
                webhook.Data.Description2);
        }

        private static PayOsProviderPayment Pending(long orderCode, long amount, string? url, long? expiresAt) =>
            new(orderCode, amount, 0, amount, "PENDING", $"test-link-{orderCode}", url, expiresAt, null, null);
    }
}

