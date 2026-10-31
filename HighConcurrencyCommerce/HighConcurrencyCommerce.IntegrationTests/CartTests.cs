using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class CartTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "A secure cart pass 123!";
    private static readonly Guid InStockSku = Guid.Parse("30000000-0000-0000-0000-000000000002");
    private static readonly Guid OutOfStockSku = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid DraftSku = Guid.Parse("30000000-0000-0000-0000-000000000006");
    private static readonly Guid InactiveSku = Guid.Parse("30000000-0000-0000-0000-000000000003");

    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("requestToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, "/api/v1/" + path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        return await client.SendAsync(request);
    }

    private async Task<(HttpClient Client, string Email)> CreateCustomer(string? email = null)
    {
        email ??= $"cart_{Guid.NewGuid():N}@example.invalid";
        var client = fixture.CreateClient();
        using var register = await Send(client, HttpMethod.Post, "auth/register",
            new { email, password = Password, confirmPassword = Password, fullName = "Cart Customer" });
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        using var login = await Send(client, HttpMethod.Post, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (client, email);
    }

    private async Task<HttpClient> CreateRoleClient(string roleKey)
    {
        var email = $"cart_{roleKey}_{Guid.NewGuid():N}@example.invalid";
        await using var db = await fixture.OpenDatabaseAsync();
        await using var role = new NpgsqlCommand("SELECT id FROM store.roles WHERE role_key = @role", db);
        role.Parameters.AddWithValue("role", roleKey);
        var roleId = (Guid)(await role.ExecuteScalarAsync())!;
        var hash = new PasswordHasher<object>().HashPassword(new object(), Password);
        await using var insert = new NpgsqlCommand(
            "INSERT INTO store.user_accounts (id, role_id, email, password_hash, full_name, account_status) " +
            "VALUES (gen_random_uuid(), @role, @email, @hash, 'Cart role test', 'ACTIVE')", db);
        insert.Parameters.AddWithValue("role", roleId);
        insert.Parameters.AddWithValue("email", email);
        insert.Parameters.AddWithValue("hash", hash);
        await insert.ExecuteNonQueryAsync();
        var client = fixture.CreateClient();
        using var login = await Send(client, HttpMethod.Post, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    [Fact]
    public async Task Cart_endpoints_are_customer_only()
    {
        using var guest = fixture.CreateClient();
        using var staff = await CreateRoleClient("STAFF");
        using var admin = await CreateRoleClient("ADMIN");
        var (customer, _) = await CreateCustomer();
        using (customer)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/v1/cart")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/api/v1/cart")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/cart")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync("/api/v1/cart")).StatusCode);
        }
    }

    [Fact]
    public async Task Customer_can_set_update_remove_items_and_carts_are_owner_isolated()
    {
        var (customerA, _) = await CreateCustomer();
        var (customerB, _) = await CreateCustomer();
        using (customerA)
        using (customerB)
        {
            using var add = await Send(customerA, HttpMethod.Put, $"cart/items/{InStockSku}", new { quantity = 2 });
            Assert.Equal(HttpStatusCode.OK, add.StatusCode);
            var added = await add.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(2, added.GetProperty("totalQuantity").GetInt32());
            Assert.Equal(3500000m, added.GetProperty("items")[0].GetProperty("unitPrice").GetDecimal());

            var other = await customerB.GetFromJsonAsync<JsonElement>("/api/v1/cart");
            Assert.Equal(0, other.GetProperty("items").GetArrayLength());

            using var update = await Send(customerA, HttpMethod.Put, $"cart/items/{InStockSku}", new { quantity = 1 });
            Assert.Equal(1, (await update.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("totalQuantity").GetInt32());
            using var remove = await Send(customerA, HttpMethod.Delete, $"cart/items/{InStockSku}");
            Assert.Equal(0, (await remove.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").GetArrayLength());
        }
    }

    [Fact]
    public async Task Set_item_rejects_draft_inactive_and_invalid_quantities()
    {
        var (customer, _) = await CreateCustomer();
        using (customer)
        {
            using var draft = await Send(customer, HttpMethod.Put, $"cart/items/{DraftSku}", new { quantity = 1 });
            Assert.Equal(HttpStatusCode.NotFound, draft.StatusCode);
            Assert.Equal("SKU_NOT_AVAILABLE", (await draft.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            using var inactive = await Send(customer, HttpMethod.Put, $"cart/items/{InactiveSku}", new { quantity = 1 });
            Assert.Equal(HttpStatusCode.NotFound, inactive.StatusCode);
            using var invalid = await Send(customer, HttpMethod.Put, $"cart/items/{InStockSku}", new { quantity = 100 });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
    }

    [Fact]
    public async Task Merge_adds_valid_guest_items_and_reports_stale_skus()
    {
        var (customer, _) = await CreateCustomer();
        using (customer)
        {
            using var existing = await Send(customer, HttpMethod.Put, $"cart/items/{InStockSku}", new { quantity = 4 });
            Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
            using var merge = await Send(customer, HttpMethod.Post, "cart/merge", new
            {
                items = new[]
                {
                    new { productSkuId = InStockSku, quantity = 2 },
                    new { productSkuId = InStockSku, quantity = 3 },
                    new { productSkuId = DraftSku, quantity = 1 }
                }
            });
            Assert.Equal(HttpStatusCode.OK, merge.StatusCode);
            var body = await merge.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(9, body.GetProperty("cart").GetProperty("totalQuantity").GetInt32());
            Assert.Equal("SKU_NOT_AVAILABLE", body.GetProperty("issues")[0].GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Validate_rechecks_stock_without_reserving_or_mutating_inventory()
    {
        var (customer, _) = await CreateCustomer();
        using (customer)
        {
            using var add = await Send(customer, HttpMethod.Put, $"cart/items/{OutOfStockSku}", new { quantity = 1 });
            Assert.Equal(HttpStatusCode.OK, add.StatusCode);
            using var validate = await Send(customer, HttpMethod.Post, "cart/validate");
            var body = await validate.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(body.GetProperty("isValid").GetBoolean());
            Assert.Equal("INSUFFICIENT_STOCK", body.GetProperty("issues")[0].GetProperty("code").GetString());
        }

        await using var db = await fixture.OpenDatabaseAsync();
        await using var query = new NpgsqlCommand(
            "SELECT physical_qty, available_qty, reserved_qty, allocated_qty FROM store.inventory_balances WHERE product_sku_id = @sku", db);
        query.Parameters.AddWithValue("sku", OutOfStockSku);
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(10, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.Equal(5, reader.GetInt32(2));
        Assert.Equal(5, reader.GetInt32(3));
    }

    [Fact]
    public async Task Concurrent_merges_serialize_on_customer_and_do_not_lose_quantity()
    {
        var email = $"cart_race_{Guid.NewGuid():N}@example.invalid";
        var (clientA, _) = await CreateCustomer(email);
        var clientB = fixture.CreateClient();
        using (clientA)
        using (clientB)
        {
            using var login = await Send(clientB, HttpMethod.Post, "auth/login", new { email, password = Password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var first = Send(clientA, HttpMethod.Post, "cart/merge", new { items = new[] { new { productSkuId = InStockSku, quantity = 10 } } });
            var second = Send(clientB, HttpMethod.Post, "cart/merge", new { items = new[] { new { productSkuId = InStockSku, quantity = 20 } } });
            var responses = await Task.WhenAll(first, second);
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            foreach (var response in responses) response.Dispose();
            var cart = await clientA.GetFromJsonAsync<JsonElement>("/api/v1/cart");
            Assert.Equal(30, cart.GetProperty("totalQuantity").GetInt32());
        }
    }

    [Fact]
    public async Task Cart_persists_flash_sale_offer_uses_sale_price_and_invalidates_after_campaign_cancel()
    {
        var (customer, _) = await CreateCustomer();
        using (customer)
        {
            var campaignId = Guid.NewGuid();
            var offerId = Guid.NewGuid();
            await using (var db = await fixture.OpenDatabaseAsync())
            {
                await using var command = new NpgsqlCommand("""
                    INSERT INTO store.flash_sale_campaigns(id,name,starts_at,ends_at,campaign_status,created_by)
                    SELECT @campaign,'Cart Flash Sale',now()-interval '1 hour',now()+interval '1 hour','ACTIVE',id
                    FROM store.user_accounts LIMIT 1;
                    INSERT INTO store.flash_sale_offers(id,campaign_id,product_sku_id,sale_price,quota_total,per_customer_limit)
                    VALUES(@offer,@campaign,@sku,2500000,5,2);
                    """, db);
                command.Parameters.AddWithValue("campaign", campaignId);
                command.Parameters.AddWithValue("offer", offerId);
                command.Parameters.AddWithValue("sku", InStockSku);
                await command.ExecuteNonQueryAsync();
            }

            using var add = await Send(customer, HttpMethod.Put, $"cart/items/{InStockSku}", new
            {
                quantity = 1, flashSaleOfferId = offerId
            });
            Assert.Equal(HttpStatusCode.OK, add.StatusCode);
            var item = (await add.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0];
            Assert.Equal(offerId, item.GetProperty("flashSaleOfferId").GetGuid());
            Assert.Equal(2500000m, item.GetProperty("unitPrice").GetDecimal());

            await using (var db = await fixture.OpenDatabaseAsync())
            await using (var cancel = new NpgsqlCommand(
                "UPDATE store.flash_sale_campaigns SET campaign_status='CANCELLED' WHERE id=@id", db))
            {
                cancel.Parameters.AddWithValue("id", campaignId);
                await cancel.ExecuteNonQueryAsync();
            }
            using var validate = await Send(customer, HttpMethod.Post, "cart/validate");
            var validation = await validate.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(validation.GetProperty("isValid").GetBoolean());
            Assert.Equal("FLASH_SALE_NOT_AVAILABLE", validation.GetProperty("issues")[0].GetProperty("code").GetString());
        }
    }
}
