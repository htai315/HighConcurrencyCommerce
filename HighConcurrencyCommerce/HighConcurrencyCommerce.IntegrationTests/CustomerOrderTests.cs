using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class CustomerOrderTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "A secure customer order pass 123!";

    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("requestToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string path, object? body = null, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/" + path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private async Task<(HttpClient Client, Guid UserId, Guid AddressId)> CreateCustomer()
    {
        var email = $"orders_{Guid.NewGuid():N}@example.invalid";
        var client = fixture.CreateClient();
        using var register = await Send(client, "auth/register", new { email, password = Password, confirmPassword = Password, fullName = "Order Customer" });
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        using var login = await Send(client, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var addressResponse = await Send(client, "account/addresses", new
        {
            recipientName = "Người nhận order", phone = "0901234567", addressLine = "31 Đường Đơn Hàng",
            ward = "Phường Cầu", province = "Hà Nội", isDefault = true
        });
        var address = await addressResponse.Content.ReadFromJsonAsync<JsonElement>();
        await using var db = await fixture.OpenDatabaseAsync();
        await using var query = new NpgsqlCommand("SELECT id FROM store.user_accounts WHERE email=@email", db);
        query.Parameters.AddWithValue("email", email);
        return (client, (Guid)(await query.ExecuteScalarAsync())!, address.GetProperty("id").GetGuid());
    }

    private async Task<HttpClient> CreateStaff()
    {
        var email = $"orders_staff_{Guid.NewGuid():N}@example.invalid";
        await using var db = await fixture.OpenDatabaseAsync();
        await using var role = new NpgsqlCommand("SELECT id FROM store.roles WHERE role_key='STAFF'", db);
        var roleId = (Guid)(await role.ExecuteScalarAsync())!;
        var hash = new PasswordHasher<object>().HashPassword(new object(), Password);
        await using var insert = new NpgsqlCommand(
            "INSERT INTO store.user_accounts(id,role_id,email,password_hash,full_name,account_status) VALUES(gen_random_uuid(),@role,@email,@hash,'Order Staff','ACTIVE')", db);
        insert.Parameters.AddWithValue("role", roleId); insert.Parameters.AddWithValue("email", email); insert.Parameters.AddWithValue("hash", hash);
        await insert.ExecuteNonQueryAsync();
        var client = fixture.CreateClient();
        using var login = await Send(client, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    private async Task<Guid> CreateSku(Guid actorId, int available = 5)
    {
        var productId = Guid.NewGuid(); var skuId = Guid.NewGuid();
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO store.products(id,title,publication_status,created_by,published_by,published_at)
            VALUES(@product,'Vợt Customer Order','PUBLISHED',@actor,@actor,now());
            INSERT INTO store.product_skus(id,product_id,sku_code,option_label,regular_price,sku_status)
            VALUES(@sku,@product,@code,'4U G5',350000,'ACTIVE');
            INSERT INTO store.inventory_balances(id,product_sku_id,physical_qty,available_qty,reserved_qty,allocated_qty,revision)
            VALUES(gen_random_uuid(),@sku,@available,@available,0,0,0);
            """, db);
        command.Parameters.AddWithValue("product", productId); command.Parameters.AddWithValue("sku", skuId);
        command.Parameters.AddWithValue("actor", actorId); command.Parameters.AddWithValue("code", "ORD-" + skuId.ToString("N")[..10]);
        command.Parameters.AddWithValue("available", available);
        await command.ExecuteNonQueryAsync();
        return skuId;
    }

    private static object CheckoutPayload(Guid addressId, Guid skuId, int quantity = 2) => new
    {
        shippingAddressId = addressId, paymentMethod = "COD",
        lines = new[] { new { productSkuId = skuId, quantity, flashSaleOfferId = (Guid?)null } }
    };

    private async Task<Guid> Checkout(HttpClient customer, Guid addressId, Guid skuId, int quantity = 2)
    {
        using var response = await Send(customer, "checkouts", CheckoutPayload(addressId, skuId, quantity), "order-center-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid();
    }

    [Fact]
    public async Task Order_list_and_detail_are_customer_only_and_owner_isolated()
    {
        using var guest = fixture.CreateClient();
        using var staff = await CreateStaff();
        var (owner, ownerId, ownerAddress) = await CreateCustomer();
        var (other, _, _) = await CreateCustomer();
        using (owner) using (other)
        {
            var skuId = await CreateSku(ownerId);
            var orderId = await Checkout(owner, ownerAddress, skuId);
            Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/v1/orders")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/api/v1/orders")).StatusCode);
            using var listResponse = await owner.GetAsync("/api/v1/orders");
            Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode); Assert.True(listResponse.Headers.CacheControl?.NoStore);
            var list = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
            var item = Assert.Single(list.GetProperty("items").EnumerateArray());
            Assert.Equal(orderId, item.GetProperty("id").GetGuid());
            Assert.Equal("Vợt Customer Order", item.GetProperty("items")[0].GetProperty("productTitle").GetString());
            Assert.Equal("31 Đường Đơn Hàng, Phường Cầu, Hà Nội", item.GetProperty("shippingAddress").GetString());
            using var detail = await owner.GetAsync($"/api/v1/orders/{orderId}");
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            using var crossOwner = await other.GetAsync($"/api/v1/orders/{orderId}");
            Assert.Equal(HttpStatusCode.NotFound, crossOwner.StatusCode);
        }
    }

    [Fact]
    public async Task Customer_cancel_releases_stock_voids_payment_and_replays_without_duplicate_side_effects()
    {
        var (customer, customerId, addressId) = await CreateCustomer();
        using (customer)
        {
            var skuId = await CreateSku(customerId);
            var orderId = await Checkout(customer, addressId, skuId);
            using var invalid = await Send(customer, $"orders/{orderId}/cancel", new { reason = " a " });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("INVALID_ARGUMENT", (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            var payload = new { reason = "Tôi đặt nhầm phiên bản vợt" };
            using var cancelled = await Send(customer, $"orders/{orderId}/cancel", payload);
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
            var body = await cancelled.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("CANCELLED", body.GetProperty("orderStatus").GetString());
            Assert.Equal("VOID", body.GetProperty("paymentStatus").GetString());
            Assert.Equal("RELEASED", body.GetProperty("reservationStatus").GetString());
            Assert.False(body.GetProperty("canCancel").GetBoolean());
            using var replay = await Send(customer, $"orders/{orderId}/cancel", payload);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

            await using var db = await fixture.OpenDatabaseAsync();
            await using var query = new NpgsqlCommand("""
                SELECT physical_qty,available_qty,reserved_qty,allocated_qty FROM store.inventory_balances WHERE product_sku_id=@sku;
                SELECT count(*) FROM store.inventory_movements WHERE source_id=@order AND movement_type='RELEASE';
                SELECT count(*) FROM store.order_cancellation_requests WHERE order_id=@order AND decision='APPROVED';
                SELECT count(*) FROM store.audit_logs WHERE entity_id=@order AND action='CUSTOMER_ORDER_CANCELLED';
                SELECT count(*) FROM store.in_app_notifications WHERE recipient_id=@customer AND event_key=@event;
                """, db);
            query.Parameters.AddWithValue("sku", skuId); query.Parameters.AddWithValue("order", orderId);
            query.Parameters.AddWithValue("customer", customerId); query.Parameters.AddWithValue("event", $"order:{orderId:N}:customer-cancelled");
            await using var reader = await query.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            Assert.Equal(5, reader.GetInt32(0)); Assert.Equal(5, reader.GetInt32(1)); Assert.Equal(0, reader.GetInt32(2)); Assert.Equal(0, reader.GetInt32(3));
            for (var i = 0; i < 4; i++) { Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync()); Assert.Equal(1L, reader.GetInt64(0)); }
        }
    }

    [Fact]
    public async Task Confirmed_order_cannot_be_cancelled_by_customer()
    {
        var (customer, customerId, addressId) = await CreateCustomer();
        using var staff = await CreateStaff();
        using (customer)
        {
            var skuId = await CreateSku(customerId);
            var orderId = await Checkout(customer, addressId, skuId);
            using var confirmed = await Send(staff, $"staff/orders/{orderId}/confirm-cod");
            Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
            using var cancel = await Send(customer, $"orders/{orderId}/cancel", new { reason = "Muốn hủy sau xác nhận" });
            Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
            Assert.Equal("ORDER_NOT_CANCELLABLE", (await cancel.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Concurrent_customer_cancel_and_staff_confirm_only_one_transition_wins()
    {
        var (customer, customerId, addressId) = await CreateCustomer();
        using var staff = await CreateStaff();
        using (customer)
        {
            var skuId = await CreateSku(customerId);
            var orderId = await Checkout(customer, addressId, skuId);
            var responses = await Task.WhenAll(
                Send(customer, $"orders/{orderId}/cancel", new { reason = "Race cancel với confirm" }),
                Send(staff, $"staff/orders/{orderId}/confirm-cod"));
            Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
            foreach (var response in responses) response.Dispose();

            await using var db = await fixture.OpenDatabaseAsync();
            await using var query = new NpgsqlCommand("""
                SELECT o.order_status,r.reservation_status,p.payment_status,b.available_qty,b.reserved_qty,b.allocated_qty
                FROM store.sales_orders o JOIN store.stock_reservations r ON r.order_id=o.id
                JOIN store.payments p ON p.order_id=o.id JOIN store.stock_reservation_lines l ON l.reservation_id=r.id
                JOIN store.inventory_balances b ON b.product_sku_id=l.product_sku_id WHERE o.id=@order;
                """, db);
            query.Parameters.AddWithValue("order", orderId);
            await using var reader = await query.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            if (reader.GetString(0) == "CANCELLED")
            {
                Assert.Equal("RELEASED", reader.GetString(1)); Assert.Equal("VOID", reader.GetString(2));
                Assert.Equal(5, reader.GetInt32(3)); Assert.Equal(0, reader.GetInt32(4)); Assert.Equal(0, reader.GetInt32(5));
            }
            else
            {
                Assert.Equal("CONFIRMED", reader.GetString(0)); Assert.Equal("ALLOCATED", reader.GetString(1)); Assert.Equal("COD_PENDING", reader.GetString(2));
                Assert.Equal(3, reader.GetInt32(3)); Assert.Equal(0, reader.GetInt32(4)); Assert.Equal(2, reader.GetInt32(5));
            }
        }
    }
}
