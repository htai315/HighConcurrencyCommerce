using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class ShippingQuoteApiTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "A secure test pass 123!@#";
    private static readonly Guid MethodId = Guid.Parse("82000000-0000-0000-0000-000000000001");
    private static readonly Guid CampaignId = Guid.Parse("82000000-0000-0000-0000-000000000002");
    private static readonly Guid OfferId = Guid.Parse("82000000-0000-0000-0000-000000000003");
    private static readonly Guid SkuId = Guid.Parse("30000000-0000-0000-0000-000000000002");

    [Fact]
    public async Task Quote_endpoint_enforces_RBAC_and_CSRF()
    {
        await SeedPricingAsync();
        using var guest = fixture.CreateClient();
        var (customer, _, addressId, _) = await CreateRoleClient("CUSTOMER");
        using var customerClient = customer;
        var (staff, _, staffAddress, _) = await CreateRoleClient("STAFF");
        using var staffClient = staff;
        var (admin, _, adminAddress, _) = await CreateRoleClient("ADMIN");
        using var adminClient = admin;

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await guest.PostAsJsonAsync("/api/v1/shipping/quotes", QuoteBody(addressId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(staffClient, "shipping/quotes", QuoteBody(staffAddress))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(adminClient, "shipping/quotes", QuoteBody(adminAddress))).StatusCode);

        using var missingCsrf = await customerClient.PostAsJsonAsync(
            "/api/v1/shipping/quotes", QuoteBody(addressId));
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        Assert.Equal("CSRF_INVALID",
            (await missingCsrf.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        using var accepted = await Send(customerClient, "shipping/quotes", QuoteBody(addressId));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Contains("no-store", accepted.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quote_contract_is_owner_only_uses_flash_price_and_does_not_mutate_quota_or_inventory()
    {
        await SeedPricingAsync();
        var (customer, _, addressId, _) = await CreateRoleClient("CUSTOMER");
        using var client = customer;
        var (_, _, foreignAddressId, _) = await CreateRoleClient("CUSTOMER");

        var before = await ReadInventoryAndOfferAsync();
        using var response = await Send(client, "shipping/quotes", QuoteBody(addressId, OfferId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(addressId, body.GetProperty("shippingAddressId").GetGuid());
        Assert.Equal("HO CHI MINH", body.GetProperty("destinationProvinceKey").GetString());
        Assert.Equal(3_000_000m, body.GetProperty("merchandiseSubtotal").GetDecimal());
        var quote = Assert.Single(body.GetProperty("quotes").EnumerateArray());
        Assert.Equal(MethodId, quote.GetProperty("shippingMethodId").GetGuid());
        Assert.Equal("STANDARD_API", quote.GetProperty("code").GetString());
        Assert.Equal(11, quote.GetProperty("methodRevision").GetInt64());
        Assert.Equal(0m, quote.GetProperty("shippingFee").GetDecimal());
        Assert.True(quote.GetProperty("freeShippingApplied").GetBoolean());
        Assert.Equal(before, await ReadInventoryAndOfferAsync());

        using var foreign = await Send(client, "shipping/quotes", QuoteBody(foreignAddressId));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal("ADDRESS_NOT_FOUND",
            (await foreign.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        using var duplicate = await Send(client, "shipping/quotes", new
        {
            shippingAddressId = addressId,
            lines = new[]
            {
                new { productSkuId = SkuId, quantity = 1, flashSaleOfferId = (Guid?)null },
                new { productSkuId = SkuId, quantity = 1, flashSaleOfferId = (Guid?)null }
            }
        });
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Equal("DUPLICATE_SKU",
            (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Quote_endpoint_is_rate_limited_in_production_profile()
    {
        await SeedPricingAsync();
        var (registeredClient, _, addressId, email) = await CreateRoleClient("CUSTOMER");
        registeredClient.Dispose();

        await using var app = fixture.CreateRateLimitedApplication();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
        });
        using (var login = await Send(client, "auth/login", new { email, password = Password }))
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var csrf = await Csrf(client);

        for (var attempt = 1; attempt <= 30; attempt++)
        {
            using var allowed = await SendWithCsrf(client, "shipping/quotes", QuoteBody(addressId), csrf);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
        using var limited = await SendWithCsrf(client, "shipping/quotes", QuoteBody(addressId), csrf);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter is not null);
        Assert.Equal("RATE_LIMITED",
            (await limited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    private static object QuoteBody(Guid addressId, Guid? offerId = null) => new
    {
        shippingAddressId = addressId,
        lines = new[] { new { productSkuId = SkuId, quantity = 1, flashSaleOfferId = offerId } }
    };

    private async Task<(HttpClient Client, Guid UserId, Guid AddressId, string Email)> CreateRoleClient(string role)
    {
        var client = fixture.CreateClient();
        var email = $"shipping_quote_{role}_{Guid.NewGuid():N}@example.com";
        using var register = await Send(client, "auth/register", new
        {
            email, password = Password, confirmPassword = Password, fullName = $"{role} Quote Test"
        });
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        var addressId = Guid.NewGuid();
        await using var db = await fixture.OpenDatabaseAsync();
        await using (var update = new NpgsqlCommand("""
            UPDATE store.user_accounts
            SET role_id=(SELECT id FROM store.roles WHERE role_key=@role), email_verified_at=now()
            WHERE lower(email)=lower(@email)
            RETURNING id
            """, db))
        {
            update.Parameters.AddWithValue("role", role);
            update.Parameters.AddWithValue("email", email);
            var userId = (Guid)(await update.ExecuteScalarAsync())!;
            await using var address = new NpgsqlCommand("""
                INSERT INTO store.customer_addresses(id, customer_id, recipient_name, phone, address_line, province, is_default)
                VALUES(@address_id,@user_id,'Quote Test','0900000020','20 Quote','TP. Hồ Chí Minh',true)
                """, db);
            address.Parameters.AddWithValue("address_id", addressId);
            address.Parameters.AddWithValue("user_id", userId);
            await address.ExecuteNonQueryAsync();
            using var login = await Send(client, "auth/login", new { email, password = Password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            return (client, userId, addressId, email);
        }
    }

    private async Task SeedPricingAsync()
    {
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE store.shipping_methods
            SET is_default=false, method_status='INACTIVE'
            WHERE id='90000000-0000-0000-0000-000000000001';

            INSERT INTO store.shipping_methods(
                id, code, name, method_status, base_fee, free_shipping_threshold,
                estimated_min_days, estimated_max_days, is_default, display_order, revision, created_by, updated_by)
            VALUES (@method_id, 'STANDARD_API', 'Giao tiêu chuẩn API', 'ACTIVE', 20000, 3000000,
                    2, 4, true, 1, 11,
                    '00000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000002')
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO store.shipping_method_province_rates(
                shipping_method_id, province_key, surcharge_fee, is_active, created_by, updated_by)
            VALUES (@method_id, 'HO CHI MINH', 5000, true,
                    '00000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000002')
            ON CONFLICT (shipping_method_id, province_key) DO NOTHING;

            INSERT INTO store.flash_sale_campaigns(id, name, starts_at, ends_at, campaign_status, created_by)
            VALUES (@campaign_id, 'Shipping API flash sale', now()-interval '1 hour', now()+interval '1 hour', 'ACTIVE',
                    '00000000-0000-0000-0000-000000000002')
            ON CONFLICT (id) DO UPDATE SET starts_at=now()-interval '1 hour', ends_at=now()+interval '1 hour', campaign_status='ACTIVE';

            INSERT INTO store.flash_sale_offers(
                id, campaign_id, product_sku_id, sale_price, quota_total, quota_held, quota_sold, per_customer_limit)
            VALUES (@offer_id, @campaign_id, @sku_id, 3000000, 100, 0, 0, 99)
            ON CONFLICT (id) DO NOTHING;
            """, db);
        command.Parameters.AddWithValue("method_id", MethodId);
        command.Parameters.AddWithValue("campaign_id", CampaignId);
        command.Parameters.AddWithValue("offer_id", OfferId);
        command.Parameters.AddWithValue("sku_id", SkuId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<(int Available, long InventoryRevision, int Held, int Sold)> ReadInventoryAndOfferAsync()
    {
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            SELECT b.available_qty,b.revision,o.quota_held,o.quota_sold
            FROM store.inventory_balances b CROSS JOIN store.flash_sale_offers o
            WHERE b.product_sku_id=@sku_id AND o.id=@offer_id
            """, db);
        command.Parameters.AddWithValue("sku_id", SkuId);
        command.Parameters.AddWithValue("offer_id", OfferId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt32(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt32(3));
    }

    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("requestToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> Send(
        HttpClient client, string path, object? body = null, HttpMethod? method = null) =>
        await SendWithCsrf(client, path, body, await Csrf(client), method);

    private static async Task<HttpResponseMessage> SendWithCsrf(
        HttpClient client, string path, object? body, string csrf, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, "/api/v1/" + path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return await client.SendAsync(request);
    }
}
