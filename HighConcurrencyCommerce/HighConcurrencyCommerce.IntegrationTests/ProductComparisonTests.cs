using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HighConcurrencyCommerce.Infrastructure.Catalog;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class ProductComparisonTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string AlphaId = "20000000-0000-0000-0000-000000000001";
    private const string BetaId = "20000000-0000-0000-0000-000000000002";
    private const string GammaId = "20000000-0000-0000-0000-000000000003";
    private const string DraftId = "20000000-0000-0000-0000-000000000004";
    private const string ComparisonId = "20000000-0000-0000-0000-000000000008";
    private const string HiddenComparisonId = "20000000-0000-0000-0000-000000000009";

    [Fact]
    public async Task Anonymous_batch_preserves_request_order_and_public_projection()
    {
        await EnsureComparisonProductsAsync();

        using var response = await fixture.Client.GetAsync(Url(ComparisonId, AlphaId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal([ComparisonId, AlphaId], items.Select(item => item.GetProperty("id").GetString()));
        Assert.Equal(2, body.GetProperty("requestedCount").GetInt32());
        Assert.Equal(0, body.GetProperty("omittedCount").GetInt32());
        Assert.Equal("flex", items[0].GetProperty("specifications")[0].GetProperty("value").GetString());
        Assert.Equal(2, items[1].GetProperty("skus").GetArrayLength());
        Assert.DoesNotContain("physicalQty", body.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("publicationStatus", body.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INACTIVE-SECRET", body.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_public_and_missing_products_are_omitted_without_lifecycle_disclosure()
    {
        await EnsureComparisonProductsAsync();
        const string missing = "99999999-0000-0000-0000-000000000000";

        using var response = await fixture.Client.GetAsync(Url(AlphaId, DraftId, missing));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(3, body.GetProperty("requestedCount").GetInt32());
        Assert.Equal(2, body.GetProperty("omittedCount").GetInt32());
        Assert.Equal(AlphaId, body.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.DoesNotContain("DRAFT", body.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Public_products_from_different_categories_return_stable_problem_details()
    {
        using var response = await fixture.Client.GetAsync(Url(AlphaId, BetaId));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("COMPARISON_CATEGORY_MISMATCH", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("correlationId").GetString()));
    }

    [Fact]
    public async Task Products_in_same_hidden_category_compare_without_exposing_category_metadata()
    {
        await EnsureComparisonProductsAsync();

        using var response = await fixture.Client.GetAsync(Url(GammaId, HiddenComparisonId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal(2, items.Length);
        Assert.All(items, item => Assert.Equal(JsonValueKind.Null, item.GetProperty("category").ValueKind));
        Assert.DoesNotContain("Private category", body.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?id=invalid")]
    [InlineData("?id=00000000-0000-0000-0000-000000000000")]
    [InlineData("?id=20000000-0000-0000-0000-000000000001&id=20000000-0000-0000-0000-000000000001")]
    [InlineData("?id=20000000-0000-0000-0000-000000000001&id=20000000-0000-0000-0000-000000000002&id=20000000-0000-0000-0000-000000000003&id=20000000-0000-0000-0000-000000000007&id=20000000-0000-0000-0000-000000000008")]
    public async Task Invalid_id_sets_return_validation_problem_never_500(string query)
    {
        using var response = await fixture.Client.GetAsync("/api/v1/catalog/products/compare" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_ERROR", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Batch_reader_executes_one_database_command_not_one_per_product()
    {
        await EnsureComparisonProductsAsync();
        await using var connection = await fixture.OpenDatabaseAsync();
        var observer = new CommandCounter();
        var options = new DbContextOptionsBuilder<StoreDbContext>()
            .UseNpgsql(connection)
            .AddInterceptors(observer)
            .Options;
        await using var db = new StoreDbContext(options);
        var catalog = new PostgresCatalogReader(db);

        var result = await catalog.GetComparisonAsync(
            [Guid.Parse(AlphaId), Guid.Parse(ComparisonId)],
            CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        Assert.Single(observer.Commands);
        Assert.All(observer.CommandTimeouts, timeout => Assert.Equal(2, timeout));
        Assert.Null(db.Database.GetCommandTimeout());
    }

    private static string Url(params string[] ids) =>
        "/api/v1/catalog/products/compare?" + string.Join("&", ids.Select(id => "id=" + Uri.EscapeDataString(id)));

    private async Task EnsureComparisonProductsAsync()
    {
        await using var connection = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO store.products (id,title,description,publication_status,category_id,created_by,created_at)
            VALUES
              ('20000000-0000-0000-0000-000000000008','Alpha Monitor Lite','Comparison fixture','PUBLISHED','10000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000002','2026-01-02Z'),
              ('20000000-0000-0000-0000-000000000009','Gamma Headphones Lite','Hidden category comparison fixture','PUBLISHED','10000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000002','2026-01-02Z')
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO store.product_skus (id,product_id,sku_code,option_label,regular_price,sku_status)
            VALUES
              ('30000000-0000-0000-0000-000000000010','20000000-0000-0000-0000-000000000008','ALPHA-LITE','Lite',1800000,'ACTIVE'),
              ('30000000-0000-0000-0000-000000000011','20000000-0000-0000-0000-000000000009','GAMMA-LITE','Lite',1500000,'ACTIVE')
            ON CONFLICT (sku_code) DO NOTHING;

            INSERT INTO store.product_specifications (product_id,spec_key,display_name,spec_value,display_order)
            VALUES
              ('20000000-0000-0000-0000-000000000001','shaft_stiffness','Độ cứng thân','stiff',1),
              ('20000000-0000-0000-0000-000000000008','shaft_stiffness','Độ cứng thân','flex',1)
            ON CONFLICT (product_id,spec_key) DO NOTHING;
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public ConcurrentBag<string> Commands { get; } = [];
        public ConcurrentBag<int> CommandTimeouts { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            CommandTimeouts.Add(command.CommandTimeout);
            return ValueTask.FromResult(result);
        }
    }
}
