using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class CodOrderTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "A secure COD pass 123!";

    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("requestToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> Send(
        HttpClient client, string path, object? body = null, string? key = null, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, "/api/v1/" + path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private async Task<(HttpClient Client, Guid UserId, Guid AddressId, string Email)> CreateCustomer(string? email = null)
    {
        email ??= $"cod_{Guid.NewGuid():N}@example.invalid";
        var client = fixture.CreateClient();
        using var register = await Send(client, "auth/register",
            new { email, password = Password, confirmPassword = Password, fullName = "COD Customer" });
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        using var login = await Send(client, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var addressResponse = await Send(client, "account/addresses", new
        {
            recipientName = "Người nhận COD", phone = "0901234567", addressLine = "18 Đường Cầu",
            province = "Hà Nội", isDefault = true
        });
        var address = await addressResponse.Content.ReadFromJsonAsync<JsonElement>();
        await using var db = await fixture.OpenDatabaseAsync();
        await using var query = new NpgsqlCommand("SELECT id FROM store.user_accounts WHERE email=@email", db);
        query.Parameters.AddWithValue("email", email);
        return (client, (Guid)(await query.ExecuteScalarAsync())!, Guid.Parse(address.GetProperty("id").GetString()!), email);
    }

    private async Task<(HttpClient Client, Guid UserId)> CreateRoleClient(string roleKey)
    {
        var id = Guid.NewGuid();
        var email = $"cod_{roleKey}_{Guid.NewGuid():N}@example.invalid";
        await using var db = await fixture.OpenDatabaseAsync();
        await using var role = new NpgsqlCommand("SELECT id FROM store.roles WHERE role_key=@role", db);
        role.Parameters.AddWithValue("role", roleKey);
        var roleId = (Guid)(await role.ExecuteScalarAsync())!;
        var hash = new PasswordHasher<object>().HashPassword(new object(), Password);
        await using var insert = new NpgsqlCommand(
            "INSERT INTO store.user_accounts (id,role_id,email,password_hash,full_name,account_status) " +
            "VALUES (@id,@role,@email,@hash,@name,'ACTIVE')", db);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("role", roleId);
        insert.Parameters.AddWithValue("email", email);
        insert.Parameters.AddWithValue("hash", hash);
        insert.Parameters.AddWithValue("name", roleKey + " COD");
        await insert.ExecuteNonQueryAsync();
        var client = fixture.CreateClient();
        using var login = await Send(client, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (client, id);
    }

    private async Task<Guid> CreateSku(Guid actorId, int available = 20)
    {
        var product = Guid.NewGuid();
        var sku = Guid.NewGuid();
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO store.products(id,title,publication_status,created_by,published_by,published_at) " +
            "VALUES(@product,@title,'PUBLISHED',@actor,@actor,now()); " +
            "INSERT INTO store.product_skus(id,product_id,sku_code,option_label,regular_price,sku_status) " +
            "VALUES(@sku,@product,@code,'4U G5',350000,'ACTIVE'); " +
            "INSERT INTO store.inventory_balances(id,product_sku_id,physical_qty,available_qty,reserved_qty,allocated_qty,revision) " +
            "VALUES(gen_random_uuid(),@sku,@available,@available,0,0,0);", db);
        command.Parameters.AddWithValue("product", product);
        command.Parameters.AddWithValue("sku", sku);
        command.Parameters.AddWithValue("actor", actorId);
        command.Parameters.AddWithValue("title", "Vợt COD " + product.ToString("N")[..8]);
        command.Parameters.AddWithValue("code", "COD-" + sku.ToString("N")[..10].ToUpperInvariant());
        command.Parameters.AddWithValue("available", available);
        await command.ExecuteNonQueryAsync();
        return sku;
    }

    private static object CheckoutPayload(Guid address, Guid sku, int quantity = 1) => new
    {
        shippingAddressId = address, paymentMethod = "COD",
        lines = new[] { new { productSkuId = sku, quantity, flashSaleOfferId = (Guid?)null } }
    };

    private static async Task<Guid> Checkout(HttpClient client, Guid address, Guid sku, string key, int quantity = 1)
    {
        using var response = await Send(client, "checkouts", CheckoutPayload(address, sku, quantity), key);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Guid.Parse((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetString()!);
    }

    [Fact]
    public async Task Cod_operations_enforce_staff_only_rbac()
    {
        using var guest = fixture.CreateClient();
        var (customer, _, _, _) = await CreateCustomer();
        var (staff, _) = await CreateRoleClient("STAFF");
        var (admin, _) = await CreateRoleClient("ADMIN");
        using (customer) using (staff) using (admin)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/v1/staff/orders/cod-pending")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/v1/staff/orders/cod-pending")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/staff/orders/cod-pending")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/api/v1/staff/orders/cod-pending")).StatusCode);
        }
    }

    [Fact]
    public async Task Concurrent_checkouts_enforce_maximum_two_pending_cod_orders()
    {
        var email = $"cod_limit_{Guid.NewGuid():N}@example.invalid";
        var (first, customerId, address, _) = await CreateCustomer(email);
        var second = fixture.CreateClient();
        using (first) using (second)
        {
            using var login = await Send(second, "auth/login", new { email, password = Password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var sku = await CreateSku(customerId, 10);
            await Checkout(first, address, sku, "cod-limit-first");

            var responses = await Task.WhenAll(
                Send(first, "checkouts", CheckoutPayload(address, sku), "cod-limit-race-a"),
                Send(second, "checkouts", CheckoutPayload(address, sku), "cod-limit-race-b"));
            Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.Created));
            Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
            var rejected = responses.Single(x => x.StatusCode == HttpStatusCode.Conflict);
            Assert.Equal("COD_PENDING_LIMIT_EXCEEDED",
                (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            foreach (var response in responses) response.Dispose();

            await using var db = await fixture.OpenDatabaseAsync();
            await using var count = new NpgsqlCommand(
                "SELECT count(*) FROM store.sales_orders WHERE customer_id=@customer AND order_status='PENDING_COD_CONFIRMATION'", db);
            count.Parameters.AddWithValue("customer", customerId);
            Assert.Equal(2L, (long)(await count.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task Staff_confirmation_moves_reserved_to_allocated_and_writes_audit()
    {
        var (customer, customerId, address, _) = await CreateCustomer();
        var (staff, staffId) = await CreateRoleClient("STAFF");
        using (customer) using (staff)
        {
            var sku = await CreateSku(customerId, 5);
            var orderId = await Checkout(customer, address, sku, "cod-confirm-success", 2);
            var queue = await staff.GetFromJsonAsync<JsonElement>("/api/v1/staff/orders/cod-pending");
            Assert.Contains(queue.EnumerateArray(), x => x.GetProperty("orderId").GetString() == orderId.ToString());

            using var confirmed = await Send(staff, $"staff/orders/{orderId}/confirm-cod");
            Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
            var body = await confirmed.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("CONFIRMED", body.GetProperty("orderStatus").GetString());
            Assert.Equal("COD_PENDING", body.GetProperty("paymentStatus").GetString());

            await using var db = await fixture.OpenDatabaseAsync();
            await using var query = new NpgsqlCommand(
                "SELECT physical_qty,available_qty,reserved_qty,allocated_qty FROM store.inventory_balances WHERE product_sku_id=@sku; " +
                "SELECT reservation_status FROM store.stock_reservations WHERE order_id=@order; " +
                "SELECT count(*) FROM store.inventory_movements WHERE source_id=@order AND movement_type='ALLOCATE'; " +
                "SELECT count(*) FROM store.audit_logs WHERE entity_id=@order AND action='COD_ORDER_CONFIRMED' AND actor_id=@staff;", db);
            query.Parameters.AddWithValue("sku", sku);
            query.Parameters.AddWithValue("order", orderId);
            query.Parameters.AddWithValue("staff", staffId);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(5, reader.GetInt32(0)); Assert.Equal(3, reader.GetInt32(1)); Assert.Equal(0, reader.GetInt32(2)); Assert.Equal(2, reader.GetInt32(3));
            Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync()); Assert.Equal("ALLOCATED", reader.GetString(0));
            Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync()); Assert.Equal(1L, reader.GetInt64(0));
            Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync()); Assert.Equal(1L, reader.GetInt64(0));
        }
    }

    [Fact]
    public async Task Concurrent_double_confirmation_applies_exactly_once()
    {
        var (customer, customerId, address, _) = await CreateCustomer();
        var (staffA, _) = await CreateRoleClient("STAFF");
        var (staffB, _) = await CreateRoleClient("STAFF");
        using (customer) using (staffA) using (staffB)
        {
            var sku = await CreateSku(customerId, 4);
            var orderId = await Checkout(customer, address, sku, "cod-double-confirm");
            var responses = await Task.WhenAll(
                Send(staffA, $"staff/orders/{orderId}/confirm-cod"),
                Send(staffB, $"staff/orders/{orderId}/confirm-cod"));
            Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
            foreach (var response in responses) response.Dispose();

            await using var db = await fixture.OpenDatabaseAsync();
            await using var count = new NpgsqlCommand(
                "SELECT count(*) FROM store.inventory_movements WHERE source_id=@order AND movement_type='ALLOCATE'", db);
            count.Parameters.AddWithValue("order", orderId);
            Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task Expiry_worker_releases_stock_and_voids_payment_exactly_once()
    {
        var (customer, customerId, address, _) = await CreateCustomer();
        using (customer)
        {
            var sku = await CreateSku(customerId, 6);
            var orderId = await Checkout(customer, address, sku, "cod-worker-expiry", 2);
            await using (var db = await fixture.OpenDatabaseAsync())
            await using (var age = new NpgsqlCommand(
                "UPDATE store.sales_orders SET created_at=now()-interval '25 hours', deadline_at=now()-interval '1 minute' WHERE id=@order; " +
                "UPDATE store.stock_reservations SET reserved_at=now()-interval '25 hours', expires_at=now()-interval '1 minute' WHERE order_id=@order;", db))
            {
                age.Parameters.AddWithValue("order", orderId);
                await age.ExecuteNonQueryAsync();
            }

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                await using var pollDb = await fixture.OpenDatabaseAsync();
                await using var poll = new NpgsqlCommand("SELECT order_status FROM store.sales_orders WHERE id=@order", pollDb);
                poll.Parameters.AddWithValue("order", orderId);
                if ((string?)await poll.ExecuteScalarAsync() == "EXPIRED") break;
                await Task.Delay(75);
            }

            await Task.Delay(250); // allow multiple worker polls to prove idempotent terminal guard
            await using var verifyDb = await fixture.OpenDatabaseAsync();
            await using var verify = new NpgsqlCommand(
                "SELECT o.order_status,p.payment_status,r.reservation_status,o.closed_reason " +
                "FROM store.sales_orders o JOIN store.payments p ON p.order_id=o.id JOIN store.stock_reservations r ON r.order_id=o.id WHERE o.id=@order; " +
                "SELECT physical_qty,available_qty,reserved_qty,allocated_qty FROM store.inventory_balances WHERE product_sku_id=@sku; " +
                "SELECT count(*) FROM store.inventory_movements WHERE source_id=@order AND movement_type='RELEASE'; " +
                "SELECT count(*) FROM store.audit_logs WHERE entity_id=@order AND action='COD_ORDER_EXPIRED';", verifyDb);
            verify.Parameters.AddWithValue("order", orderId);
            verify.Parameters.AddWithValue("sku", sku);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("EXPIRED", reader.GetString(0)); Assert.Equal("VOID", reader.GetString(1)); Assert.Equal("RELEASED", reader.GetString(2)); Assert.Equal("COD_CONFIRMATION_EXPIRED", reader.GetString(3));
            Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync());
            Assert.Equal(6, reader.GetInt32(0)); Assert.Equal(6, reader.GetInt32(1)); Assert.Equal(0, reader.GetInt32(2)); Assert.Equal(0, reader.GetInt32(3));
            Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync()); Assert.Equal(1L, reader.GetInt64(0));
            Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync()); Assert.Equal(1L, reader.GetInt64(0));
        }
    }
}
