using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class CheckoutTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "A secure checkout pass 123!";

    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("requestToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> Send(
        HttpClient client, string path, object body, string? key = null, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, "/api/v1/" + path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private async Task<(HttpClient Client, Guid UserId, Guid AddressId, string Email)> CreateCustomer(string? email = null)
    {
        email ??= $"checkout_{Guid.NewGuid():N}@example.invalid";
        var client = fixture.CreateClient();
        using var register = await Send(client, "auth/register",
            new { email, password = Password, confirmPassword = Password, fullName = "Checkout Customer" });
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        using var login = await Send(client, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var addressResponse = await Send(client, "account/addresses", new
        {
            recipientName = "Người nhận checkout", phone = "0901234567",
            addressLine = "17 Đường Sân Cầu", ward = "Phường 1", province = "TP Hồ Chí Minh", isDefault = true
        });
        var address = await addressResponse.Content.ReadFromJsonAsync<JsonElement>();

        await using var db = await fixture.OpenDatabaseAsync();
        await using var userQuery = new NpgsqlCommand("SELECT id FROM store.user_accounts WHERE email = @email", db);
        userQuery.Parameters.AddWithValue("email", email);
        return (client, (Guid)(await userQuery.ExecuteScalarAsync())!, Guid.Parse(address.GetProperty("id").GetString()!), email);
    }

    private async Task<HttpClient> CreateRoleClient(string roleKey)
    {
        var email = $"checkout_{roleKey}_{Guid.NewGuid():N}@example.invalid";
        await using var db = await fixture.OpenDatabaseAsync();
        await using var role = new NpgsqlCommand("SELECT id FROM store.roles WHERE role_key = @role", db);
        role.Parameters.AddWithValue("role", roleKey);
        var roleId = (Guid)(await role.ExecuteScalarAsync())!;
        var hash = new PasswordHasher<object>().HashPassword(new object(), Password);
        await using var insert = new NpgsqlCommand(
            "INSERT INTO store.user_accounts (id, role_id, email, password_hash, full_name, account_status) " +
            "VALUES (gen_random_uuid(), @role, @email, @hash, 'Checkout role', 'ACTIVE')", db);
        insert.Parameters.AddWithValue("role", roleId);
        insert.Parameters.AddWithValue("email", email);
        insert.Parameters.AddWithValue("hash", hash);
        await insert.ExecuteNonQueryAsync();
        var client = fixture.CreateClient();
        using var login = await Send(client, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    private async Task<Guid> CreateSku(Guid actorId, int available, decimal price)
    {
        var productId = Guid.NewGuid();
        var skuId = Guid.NewGuid();
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO store.products (id, title, publication_status, created_by, published_by, published_at) " +
            "VALUES (@product, @title, 'PUBLISHED', @actor, @actor, now()); " +
            "INSERT INTO store.product_skus (id, product_id, sku_code, option_label, regular_price, sku_status) " +
            "VALUES (@sku, @product, @code, '4U G5', @price, 'ACTIVE'); " +
            "INSERT INTO store.inventory_balances (id, product_sku_id, physical_qty, available_qty, reserved_qty, allocated_qty, revision) " +
            "VALUES (gen_random_uuid(), @sku, @available, @available, 0, 0, 0);", db);
        command.Parameters.AddWithValue("product", productId);
        command.Parameters.AddWithValue("sku", skuId);
        command.Parameters.AddWithValue("actor", actorId);
        command.Parameters.AddWithValue("title", "Vợt checkout " + productId.ToString("N")[..8]);
        command.Parameters.AddWithValue("code", "CHK-" + skuId.ToString("N")[..10].ToUpperInvariant());
        command.Parameters.AddWithValue("price", price);
        command.Parameters.AddWithValue("available", available);
        await command.ExecuteNonQueryAsync();
        return skuId;
    }

    private static object Payload(Guid addressId, string paymentMethod, params (Guid SkuId, int Quantity)[] lines) => new
    {
        shippingAddressId = addressId,
        paymentMethod,
        lines = lines.Select(x => new { productSkuId = x.SkuId, quantity = x.Quantity, flashSaleOfferId = (Guid?)null }).ToArray()
    };

    [Fact]
    public async Task Checkout_is_customer_only_and_requires_valid_idempotency_key()
    {
        using var guest = fixture.CreateClient();
        using var staff = await CreateRoleClient("STAFF");
        using var admin = await CreateRoleClient("ADMIN");
        var (customer, customerId, addressId, _) = await CreateCustomer();
        using (customer)
        {
            var sku = await CreateSku(customerId, 2, 100000);
            var payload = Payload(addressId, "COD", (sku, 1));
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(guest, "checkouts", payload, "guest-key-123")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(staff, "checkouts", payload, "staff-key-123")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(admin, "checkouts", payload, "admin-key-123")).StatusCode);
            using var missing = await Send(customer, "checkouts", payload);
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.Equal("INVALID_IDEMPOTENCY_KEY", (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Multi_sku_cod_checkout_snapshots_and_reserves_atomically()
    {
        var (client, customerId, addressId, _) = await CreateCustomer();
        using (client)
        {
            var firstSku = await CreateSku(customerId, 7, 120000);
            var secondSku = await CreateSku(customerId, 9, 250000);
            using var cart1 = await Send(client, $"cart/items/{firstSku}", new { quantity = 2 }, method: HttpMethod.Put);
            using var cart2 = await Send(client, $"cart/items/{secondSku}", new { quantity = 3 }, method: HttpMethod.Put);
            Assert.Equal(HttpStatusCode.OK, cart1.StatusCode);
            Assert.Equal(HttpStatusCode.OK, cart2.StatusCode);

            var before = DateTime.UtcNow;
            using var response = await Send(client, "checkouts", Payload(addressId, "COD", (firstSku, 2), (secondSku, 3)), "cod-success-123");
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("PENDING_COD_CONFIRMATION", body.GetProperty("orderStatus").GetString());
            Assert.Equal("COD_PENDING", body.GetProperty("paymentStatus").GetString());
            Assert.Equal(990000m, body.GetProperty("totalAmount").GetDecimal());
            Assert.Equal(2, body.GetProperty("items").GetArrayLength());
            Assert.InRange(body.GetProperty("deadlineAt").GetDateTime(), before.AddHours(23).AddMinutes(59), before.AddHours(24).AddMinutes(1));
            var orderId = Guid.Parse(body.GetProperty("orderId").GetString()!);

            await using var db = await fixture.OpenDatabaseAsync();
            await using var query = new NpgsqlCommand(
                "SELECT count(*) FROM store.sales_order_items WHERE order_id = @order; " +
                "SELECT count(*) FROM store.stock_reservation_lines l JOIN store.stock_reservations r ON r.id=l.reservation_id WHERE r.order_id=@order; " +
                "SELECT count(*) FROM store.inventory_movements WHERE source_type='SALES_ORDER' AND source_id=@order AND movement_type='RESERVE'; " +
                "SELECT payment_status FROM store.payments WHERE order_id=@order; " +
                "SELECT shipping_address_snapshot->>'addressLine' FROM store.sales_orders WHERE id=@order;", db);
            query.Parameters.AddWithValue("order", orderId);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync()); Assert.Equal(2L, reader.GetInt64(0));
            Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync()); Assert.Equal(2L, reader.GetInt64(0));
            Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync()); Assert.Equal(2L, reader.GetInt64(0));
            Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync()); Assert.Equal("COD_PENDING", reader.GetString(0));
            Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync()); Assert.Equal("17 Đường Sân Cầu", reader.GetString(0));

            using var cart = await client.GetAsync("/api/v1/cart");
            Assert.Equal(0, (await cart.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").GetArrayLength());
        }
    }

    [Fact]
    public async Task Payos_checkout_is_deferred_until_sandbox_contract_is_verified()
    {
        var (client, customerId, addressId, _) = await CreateCustomer();
        using (client)
        {
            var sku = await CreateSku(customerId, 3, 300000);
            using var response = await Send(client, "checkouts", Payload(addressId, "PAYOS", (sku, 1)), "payos-core-123");
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("PAYOS_DEFERRED", body.GetProperty("code").GetString());
            await using var db = await fixture.OpenDatabaseAsync();
            await using var count = new NpgsqlCommand("SELECT count(*) FROM store.sales_orders WHERE customer_id=@customer", db);
            count.Parameters.AddWithValue("customer", customerId);
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task Insufficient_one_sku_rolls_back_every_side_effect_and_replays_rejection()
    {
        var (client, customerId, addressId, _) = await CreateCustomer();
        using (client)
        {
            var enoughSku = await CreateSku(customerId, 10, 100000);
            var shortSku = await CreateSku(customerId, 1, 200000);
            var payload = Payload(addressId, "COD", (enoughSku, 4), (shortSku, 2));
            using var rejected = await Send(client, "checkouts", payload, "reject-stock-123");
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            Assert.Equal("OUT_OF_STOCK", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

            await using (var db = await fixture.OpenDatabaseAsync())
            {
                await using var verify = new NpgsqlCommand(
                    "SELECT count(*) FROM store.sales_orders WHERE customer_id=@customer; " +
                    "SELECT available_qty, reserved_qty FROM store.inventory_balances WHERE product_sku_id=@enough; " +
                    "SELECT request_status FROM store.checkout_requests WHERE customer_id=@customer AND idempotency_key='reject-stock-123';", db);
                verify.Parameters.AddWithValue("customer", customerId);
                verify.Parameters.AddWithValue("enough", enoughSku);
                await using var reader = await verify.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync()); Assert.Equal(0L, reader.GetInt64(0));
                Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync()); Assert.Equal(10, reader.GetInt32(0)); Assert.Equal(0, reader.GetInt32(1));
                Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync()); Assert.Equal("REJECTED", reader.GetString(0));
            }

            await using (var db = await fixture.OpenDatabaseAsync())
            await using (var restock = new NpgsqlCommand(
                "UPDATE store.inventory_balances SET physical_qty=10, available_qty=10 WHERE product_sku_id=@sku", db))
            {
                restock.Parameters.AddWithValue("sku", shortSku);
                await restock.ExecuteNonQueryAsync();
            }
            using var replay = await Send(client, "checkouts", payload, "reject-stock-123");
            Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
            Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        }
    }

    [Fact]
    public async Task Success_replays_same_response_and_same_key_different_payload_conflicts()
    {
        var (client, customerId, addressId, _) = await CreateCustomer();
        using (client)
        {
            var sku = await CreateSku(customerId, 5, 100000);
            var payload = Payload(addressId, "COD", (sku, 1));
            using var first = await Send(client, "checkouts", payload, "idem-success-123");
            var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
            using var replay = await Send(client, "checkouts", payload, "idem-success-123");
            var replayBody = await replay.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal(firstBody.GetProperty("orderId").GetString(), replayBody.GetProperty("orderId").GetString());
            Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
            using var conflict = await Send(client, "checkouts", Payload(addressId, "COD", (sku, 2)), "idem-success-123");
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("IDEMPOTENCY_KEY_REUSED", (await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Concurrent_same_key_creates_exactly_one_order()
    {
        var email = $"checkout_same_{Guid.NewGuid():N}@example.invalid";
        var (firstClient, customerId, addressId, _) = await CreateCustomer(email);
        var secondClient = fixture.CreateClient();
        using (firstClient)
        using (secondClient)
        {
            using var login = await Send(secondClient, "auth/login", new { email, password = Password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var sku = await CreateSku(customerId, 5, 110000);
            var payload = Payload(addressId, "COD", (sku, 1));
            var responses = await Task.WhenAll(
                Send(firstClient, "checkouts", payload, "concurrent-idem-123"),
                Send(secondClient, "checkouts", payload, "concurrent-idem-123"));
            Assert.Contains(responses, x => x.StatusCode == HttpStatusCode.Created);
            Assert.Contains(responses, x => x.StatusCode == HttpStatusCode.OK);
            foreach (var response in responses) response.Dispose();
            await using var db = await fixture.OpenDatabaseAsync();
            await using var count = new NpgsqlCommand("SELECT count(*) FROM store.sales_orders WHERE customer_id=@customer", db);
            count.Parameters.AddWithValue("customer", customerId);
            Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task Concurrent_customers_competing_for_last_unit_never_oversell()
    {
        var (firstClient, firstId, firstAddress, _) = await CreateCustomer();
        var (secondClient, _, secondAddress, _) = await CreateCustomer();
        using (firstClient)
        using (secondClient)
        {
            var sku = await CreateSku(firstId, 1, 500000);
            var responses = await Task.WhenAll(
                Send(firstClient, "checkouts", Payload(firstAddress, "COD", (sku, 1)), "last-unit-a-123"),
                Send(secondClient, "checkouts", Payload(secondAddress, "COD", (sku, 1)), "last-unit-b-123"));
            Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.Created));
            Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
            foreach (var response in responses) response.Dispose();

            await using var db = await fixture.OpenDatabaseAsync();
            await using var query = new NpgsqlCommand(
                "SELECT physical_qty, available_qty, reserved_qty, allocated_qty FROM store.inventory_balances WHERE product_sku_id=@sku; " +
                "SELECT count(*) FROM store.sales_order_items WHERE product_sku_id=@sku;", db);
            query.Parameters.AddWithValue("sku", sku);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt32(0)); Assert.Equal(0, reader.GetInt32(1)); Assert.Equal(1, reader.GetInt32(2)); Assert.Equal(0, reader.GetInt32(3));
            Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync()); Assert.Equal(1L, reader.GetInt64(0));
        }
    }
}
