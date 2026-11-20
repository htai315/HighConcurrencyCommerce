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

public sealed class RelatedProductsTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string SourceId = "53000000-0000-0000-0000-000000000001";
    private const string BrandAId = "53000000-0000-0000-0000-0000000000b1";
    private const string BrandBId = "53000000-0000-0000-0000-0000000000b2";
    private const string CategoryId = "53000000-0000-0000-0000-0000000000c1";
    private const string OtherCategoryId = "53000000-0000-0000-0000-0000000000c2";

    // Candidates in same category
    private const string InStockSameBrandId = "53000000-0000-0000-0000-000000000002"; // in-stock, same brand, price 2.6M
    private const string InStockDiffBrandCloserPriceId = "53000000-0000-0000-0000-000000000003"; // in-stock, diff brand, price 2.51M
    private const string InStockDiffBrandFartherPriceId = "53000000-0000-0000-0000-000000000004"; // in-stock, diff brand, price 3.5M
    private const string OutOfStockSameBrandId = "53000000-0000-0000-0000-000000000005"; // out-of-stock, same brand
    private const string OtherCategoryCandidateId = "53000000-0000-0000-0000-000000000006"; // different category
    private const string SuspendedCandidateId = "53000000-0000-0000-0000-000000000007"; // SUSPENDED

    [Fact]
    public async Task Anonymous_related_request_succeeds_without_auth_or_csrf()
    {
        await EnsureRelatedFixtureAsync();

        using var response = await fixture.Client.GetAsync($"/api/v1/catalog/products/{SourceId}/related");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(SourceId, body.GetProperty("sourceProductId").GetString());
        var items = body.GetProperty("items").EnumerateArray().ToArray();
        Assert.NotEmpty(items);

        // Does not leak internal or non-public fields
        Assert.DoesNotContain("publicationStatus", body.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("physicalQty", body.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("score", body.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_or_non_public_source_returns_generic_404()
    {
        await EnsureRelatedFixtureAsync();

        var missingId = Guid.NewGuid();
        using var missingResponse = await fixture.Client.GetAsync($"/api/v1/catalog/products/{missingId}/related");
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);

        // Suspended source product returns 404
        using var suspendedResponse = await fixture.Client.GetAsync($"/api/v1/catalog/products/{SuspendedCandidateId}/related");
        Assert.Equal(HttpStatusCode.NotFound, suspendedResponse.StatusCode);

        using var emptyResponse = await fixture.Client.GetAsync("/api/v1/catalog/products/00000000-0000-0000-0000-000000000000/related");
        Assert.Equal(HttpStatusCode.NotFound, emptyResponse.StatusCode);
    }

    [Fact]
    public async Task Candidates_only_match_same_underlying_category_and_exclude_source()
    {
        await EnsureRelatedFixtureAsync();

        using var response = await fixture.Client.GetAsync($"/api/v1/catalog/products/{SourceId}/related?limit=12");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var itemIds = body.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetString())
            .ToList();

        // Source product itself must not be in related items
        Assert.DoesNotContain(SourceId, itemIds);

        // Product from other category must not be in related items
        Assert.DoesNotContain(OtherCategoryCandidateId, itemIds);

        // Suspended product must not be in related items
        Assert.DoesNotContain(SuspendedCandidateId, itemIds);
    }

    [Fact]
    public async Task Ranking_orders_instock_then_same_brand_then_price_distance()
    {
        await EnsureRelatedFixtureAsync();

        using var response = await fixture.Client.GetAsync($"/api/v1/catalog/products/{SourceId}/related?limit=12");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var itemIds = body.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetString())
            .ToList();

        // 1. In-stock products must come before out-of-stock products
        var inStockSameBrandIndex = itemIds.IndexOf(InStockSameBrandId);
        var inStockDiffBrandCloserIndex = itemIds.IndexOf(InStockDiffBrandCloserPriceId);
        var inStockDiffBrandFartherIndex = itemIds.IndexOf(InStockDiffBrandFartherPriceId);
        var outOfStockIndex = itemIds.IndexOf(OutOfStockSameBrandId);

        Assert.True(inStockSameBrandIndex >= 0);
        Assert.True(outOfStockIndex >= 0);
        Assert.True(inStockSameBrandIndex < outOfStockIndex, "In-stock must come before out-of-stock");

        // 2. Among in-stock, same-brand comes before different-brand
        Assert.True(inStockSameBrandIndex < inStockDiffBrandCloserIndex, "Same brand must come before different brand");

        // 3. Among same in-stock & different-brand, closer price distance comes before farther price distance
        Assert.True(inStockDiffBrandCloserIndex < inStockDiffBrandFartherIndex, "Closer price distance must come before farther price distance");
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=13")]
    [InlineData("?limit=-5")]
    public async Task Limit_outside_one_to_twelve_returns_validation_problem(string query)
    {
        using var response = await fixture.Client.GetAsync($"/api/v1/catalog/products/{SourceId}/related" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_ERROR", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Reader_executes_at_most_two_database_commands_with_restored_timeout()
    {
        await EnsureRelatedFixtureAsync();
        await using var connection = await fixture.OpenDatabaseAsync();
        var observer = new CommandCounter();
        var options = new DbContextOptionsBuilder<StoreDbContext>()
            .UseNpgsql(connection)
            .AddInterceptors(observer)
            .Options;
        await using var db = new StoreDbContext(options);
        var reader = new PostgresCatalogReader(db);

        var result = await reader.GetRelatedProductsAsync(Guid.Parse(SourceId), 6, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(observer.Commands.Count <= 2, $"Expected <= 2 database commands, but got {observer.Commands.Count}");
        Assert.All(observer.CommandTimeouts, timeout => Assert.Equal(2, timeout));
        Assert.Null(db.Database.GetCommandTimeout());
    }

    private async Task EnsureRelatedFixtureAsync()
    {
        await using var connection = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO store.categories (id,name,slug,is_visible) VALUES
              ('53000000-0000-0000-0000-0000000000c1','Vợt Discovery','vot-discovery',true),
              ('53000000-0000-0000-0000-0000000000c2','Giày Discovery','giay-discovery',true)
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO store.brands (id,name,slug,country,is_active) VALUES
              ('53000000-0000-0000-0000-0000000000b1','Brand Astro','brand-astro','Nhật Bản',true),
              ('53000000-0000-0000-0000-0000000000b2','Brand Bolt','brand-bolt','Đài Loan',true)
            ON CONFLICT (id) DO NOTHING;

            -- Source product: price 2.5M, Brand Astro, Category Vợt
            INSERT INTO store.products (id,title,description,publication_status,category_id,brand_id,created_by,created_at)
            VALUES
              ('53000000-0000-0000-0000-000000000001','Vợt Astro 100','Source product','PUBLISHED','53000000-0000-0000-0000-0000000000c1','53000000-0000-0000-0000-0000000000b1','00000000-0000-0000-0000-000000000002','2026-01-01Z'),
              ('53000000-0000-0000-0000-000000000002','Vợt Astro 88','InStock SameBrand','PUBLISHED','53000000-0000-0000-0000-0000000000c1','53000000-0000-0000-0000-0000000000b1','00000000-0000-0000-0000-000000000002','2026-01-02Z'),
              ('53000000-0000-0000-0000-000000000003','Vợt Bolt 99','InStock DiffBrand CloserPrice','PUBLISHED','53000000-0000-0000-0000-0000000000c1','53000000-0000-0000-0000-0000000000b2','00000000-0000-0000-0000-000000000002','2026-01-03Z'),
              ('53000000-0000-0000-0000-000000000004','Vợt Bolt 77','InStock DiffBrand FartherPrice','PUBLISHED','53000000-0000-0000-0000-0000000000c1','53000000-0000-0000-0000-0000000000b2','00000000-0000-0000-0000-000000000002','2026-01-04Z'),
              ('53000000-0000-0000-0000-000000000005','Vợt Astro 55','OutOfStock SameBrand','PUBLISHED','53000000-0000-0000-0000-0000000000c1','53000000-0000-0000-0000-0000000000b1','00000000-0000-0000-0000-000000000002','2026-01-05Z'),
              ('53000000-0000-0000-0000-000000000006','Giày Bolt Pro','Other category','PUBLISHED','53000000-0000-0000-0000-0000000000c2','53000000-0000-0000-0000-0000000000b2','00000000-0000-0000-0000-000000000002','2026-01-06Z'),
              ('53000000-0000-0000-0000-000000000007','Vợt Astro Suspended','Suspended candidate','SUSPENDED','53000000-0000-0000-0000-0000000000c1','53000000-0000-0000-0000-0000000000b1','00000000-0000-0000-0000-000000000002','2026-01-07Z')
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO store.product_skus (id,product_id,sku_code,option_label,regular_price,sku_status)
            VALUES
              ('53000000-0000-0000-0000-0000000000a1','53000000-0000-0000-0000-000000000001','SKU-53-01','4U',2500000,'ACTIVE'),
              ('53000000-0000-0000-0000-0000000000a2','53000000-0000-0000-0000-000000000002','SKU-53-02','4U',2600000,'ACTIVE'),
              ('53000000-0000-0000-0000-0000000000a3','53000000-0000-0000-0000-000000000003','SKU-53-03','4U',2510000,'ACTIVE'),
              ('53000000-0000-0000-0000-0000000000a4','53000000-0000-0000-0000-000000000004','SKU-53-04','4U',3500000,'ACTIVE'),
              ('53000000-0000-0000-0000-0000000000a5','53000000-0000-0000-0000-000000000005','SKU-53-05','4U',2490000,'ACTIVE'),
              ('53000000-0000-0000-0000-0000000000a6','53000000-0000-0000-0000-000000000006','SKU-53-06','40',1500000,'ACTIVE'),
              ('53000000-0000-0000-0000-0000000000a7','53000000-0000-0000-0000-000000000007','SKU-53-07','4U',2500000,'ACTIVE')
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO store.inventory_balances (product_sku_id,physical_qty,available_qty,reserved_qty,allocated_qty)
            VALUES
              ('53000000-0000-0000-0000-0000000000a1',5,5,0,0),
              ('53000000-0000-0000-0000-0000000000a2',5,5,0,0),
              ('53000000-0000-0000-0000-0000000000a3',5,5,0,0),
              ('53000000-0000-0000-0000-0000000000a4',5,5,0,0),
              ('53000000-0000-0000-0000-0000000000a5',0,0,0,0), -- out of stock
              ('53000000-0000-0000-0000-0000000000a6',5,5,0,0),
              ('53000000-0000-0000-0000-0000000000a7',5,5,0,0)
            ON CONFLICT (product_sku_id) DO NOTHING;
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
