using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HighConcurrencyCommerce.Infrastructure.Auth;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class DemoSeedAcceptanceTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string DemoPassword = "DemoPass123!@#";

    [Fact]
    public async Task Demo_seed_is_idempotent_and_enables_login_for_all_three_roles()
    {
        await using var db = await fixture.OpenDatabaseAsync();

        // 1. Read DemoSeed.sql embedded resource
        await using var stream = typeof(AdminBootstrap).Assembly.GetManifestResourceStream("DemoSeed.sql")
            ?? throw new InvalidOperationException("Missing DemoSeed.sql embedded resource");
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync();

        // 2. Execute DemoSeed.sql first time
        await using (var cmd1 = new NpgsqlCommand(sql, db))
        {
            await cmd1.ExecuteNonQueryAsync();
        }

        // 3. Execute DemoSeed.sql second time (asserting absolute idempotency)
        await using (var cmd2 = new NpgsqlCommand(sql, db))
        {
            await cmd2.ExecuteNonQueryAsync();
        }

        // 4. Verify products & SKUs in database
        await using (var countCmd = new NpgsqlCommand("SELECT count(*) FROM store.products WHERE publication_status = 'PUBLISHED'", db))
        {
            var productCount = (long)(await countCmd.ExecuteScalarAsync())!;
            Assert.True(productCount >= 3);
        }

        await using (var invCmd = new NpgsqlCommand("SELECT sum(available_qty) FROM store.inventory_balances", db))
        {
            var totalAvailable = (long)(await invCmd.ExecuteScalarAsync())!;
            Assert.True(totalAvailable >= 100);
        }

        await using (var shippingCmd = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM store.shipping_methods
            WHERE method_status = 'ACTIVE' AND is_default
            """, db))
        {
            var activeDefaultCount = (long)(await shippingCmd.ExecuteScalarAsync())!;
            Assert.Equal(1, activeDefaultCount);
        }

        await using (var discoveryCmd = new NpgsqlCommand(
            """
            SELECT
                (SELECT count(*) FROM store.products p
                 JOIN store.brands b ON b.id = p.brand_id
                 WHERE p.publication_status = 'PUBLISHED' AND b.is_active),
                (SELECT count(*) FROM store.product_reviews
                 WHERE review_status = 'PUBLISHED' AND deleted_at IS NULL),
                (SELECT count(*) FROM store.sales_orders o
                 JOIN store.shipments s ON s.order_id = o.id
                 WHERE o.order_status = 'COMPLETED' AND s.shipment_status = 'DELIVERED')
            """, db))
        await using (var discoveryReader = await discoveryCmd.ExecuteReaderAsync())
        {
            Assert.True(await discoveryReader.ReadAsync());
            Assert.True(discoveryReader.GetInt64(0) >= 3);
            Assert.True(discoveryReader.GetInt64(1) >= 1);
            Assert.True(discoveryReader.GetInt64(2) >= 1);
        }

        await using (var comparisonSpecsCmd = new NpgsqlCommand(
            """
            SELECT
                count(*),
                count(DISTINCT spec_key),
                count(*) FILTER (WHERE duplicate_count > 1),
                count(*) FILTER (WHERE first_value IS DISTINCT FROM second_value)
            FROM (
                SELECT
                    spec_key,
                    count(*) OVER (PARTITION BY product_id, spec_key) AS duplicate_count,
                    max(spec_value) FILTER (WHERE product_id = 'b0000000-0000-0000-0000-000000000001')
                        OVER (PARTITION BY spec_key) AS first_value,
                    max(spec_value) FILTER (WHERE product_id = 'b0000000-0000-0000-0000-000000000002')
                        OVER (PARTITION BY spec_key) AS second_value
                FROM store.product_specifications
                WHERE product_id IN (
                    'b0000000-0000-0000-0000-000000000001',
                    'b0000000-0000-0000-0000-000000000002')
            ) specs
            """, db))
        await using (var specsReader = await comparisonSpecsCmd.ExecuteReaderAsync())
        {
            Assert.True(await specsReader.ReadAsync());
            Assert.Equal(12, specsReader.GetInt64(0));
            Assert.Equal(6, specsReader.GetInt64(1));
            Assert.Equal(0, specsReader.GetInt64(2));
            Assert.True(specsReader.GetInt64(3) > 0);
        }

        using (var comparisonResponse = await fixture.Client.GetAsync(
            "/api/v1/catalog/products/compare?id=b0000000-0000-0000-0000-000000000001&id=b0000000-0000-0000-0000-000000000002"))
        {
            Assert.Equal(HttpStatusCode.OK, comparisonResponse.StatusCode);
            var comparison = await comparisonResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(2, comparison.GetProperty("items").GetArrayLength());
            Assert.All(comparison.GetProperty("items").EnumerateArray(), item =>
                Assert.Equal(6, item.GetProperty("specifications").GetArrayLength()));
        }

        // 5. Verify all three accounts can authenticate via API
        using var client = fixture.CreateClient();

        // Admin login
        using var adminLogin = await LoginAsync(client, "admin@badminton.local", DemoPassword);
        Assert.Equal(HttpStatusCode.OK, adminLogin.StatusCode);
        var adminBody = await adminLogin.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ADMIN", adminBody.GetProperty("role").GetString());

        // Staff login
        using var staffLogin = await LoginAsync(client, "staff@badminton.local", DemoPassword);
        Assert.Equal(HttpStatusCode.OK, staffLogin.StatusCode);
        var staffBody = await staffLogin.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("STAFF", staffBody.GetProperty("role").GetString());

        // Customer login
        using var customerLogin = await LoginAsync(client, "customer@badminton.local", DemoPassword);
        Assert.Equal(HttpStatusCode.OK, customerLogin.StatusCode);
        var customerBody = await customerLogin.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CUSTOMER", customerBody.GetProperty("role").GetString());
    }

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password)
    {
        using var csrfRes = await client.GetAsync("/api/v1/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, csrfRes.StatusCode);
        var csrfBody = await csrfRes.Content.ReadFromJsonAsync<JsonElement>();
        var token = csrfBody.GetProperty("requestToken").GetString()!;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { email, password })
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        return await client.SendAsync(request);
    }
}
