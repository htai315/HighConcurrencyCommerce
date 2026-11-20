using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class CatalogSearchTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string SearchProductId = "50000000-0000-0000-0000-000000000010";

    [Fact]
    public async Task Search_matches_normalized_title_sku_option_brand_and_category()
    {
        await EnsureSearchCatalogAsync();

        var byTitle = await SearchAsync("astrox 88d prp");
        var bySku = await SearchAsync("AX88D-PRO-4U");
        var byOption = await SearchAsync("4u g5 do");
        var byBrand = await SearchAsync("yonex nhat ban");
        var byCategory = await SearchAsync("vot cau long");

        Assert.Contains(byTitle.GetProperty("items").EnumerateArray(), IsSearchProduct);
        Assert.Equal(SearchProductId, bySku.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal(2, bySku.GetProperty("totalCount").GetInt32());
        Assert.Contains(byOption.GetProperty("items").EnumerateArray(), IsSearchProduct);
        Assert.Contains(byBrand.GetProperty("items").EnumerateArray(), IsSearchProduct);
        Assert.Contains(byCategory.GetProperty("items").EnumerateArray(), IsSearchProduct);
    }

    [Theory]
    [InlineData("draft-only-needle")]
    [InlineData("suspended-only-needle")]
    [InlineData("archived-only-needle")]
    [InlineData("hidden-category-needle-971")]
    [InlineData("inactive-brand-needle-862")]
    [InlineData("zzqx-secret-917")]
    public async Task Search_never_leaks_non_public_lifecycle_or_inactive_dimensions(string query)
    {
        await EnsureSearchCatalogAsync();

        var body = await SearchAsync(query);

        Assert.True(body.GetProperty("totalCount").GetInt32() == 0, body.GetRawText());
        Assert.Empty(body.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Relevance_paging_is_deterministic_and_products_are_not_duplicated_by_skus()
    {
        await EnsureSearchCatalogAsync();

        var firstRun = await ReadPageIdsAsync("shared ranking", 1, 3);
        var secondRun = await ReadPageIdsAsync("shared ranking", 1, 3);
        var skuMatch = await SearchAsync("AX88D-PRO");

        Assert.Equal(firstRun, secondRun);
        Assert.Equal(3, firstRun.Distinct().Count());
        Assert.Equal(new[]
        {
            "50000000-0000-0000-0000-000000000020",
            "50000000-0000-0000-0000-000000000021",
            "50000000-0000-0000-0000-000000000022"
        }, firstRun);
        var skuMatchIds = skuMatch.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()).ToArray();
        Assert.Equal(skuMatchIds.Length, skuMatchIds.Distinct().Count());
        Assert.Equal(1, skuMatchIds.Count(id => id == SearchProductId));
    }

    [Fact]
    public async Task Suggestions_rank_exact_sku_before_title_and_return_public_kinds_without_duplicates()
    {
        await EnsureSearchCatalogAsync();

        var exact = await SuggestionsAsync("AX88D-PRO-4U", 8);
        var fuzzy = await SuggestionsAsync("astrox 88d prp", 8);
        var brand = await SuggestionsAsync("yonex nhat ban", 8);
        var category = await SuggestionsAsync("vot cau long", 8);

        Assert.Equal("SKU", exact[0].GetProperty("kind").GetString());
        Assert.Equal("AX88D-PRO-4U", exact[0].GetProperty("label").GetString());
        Assert.Equal(SearchProductId, exact[0].GetProperty("productId").GetString());
        Assert.Equal("PRODUCT", exact[1].GetProperty("kind").GetString());
        Assert.Contains(fuzzy, item => item.GetProperty("kind").GetString() == "PRODUCT"
            && item.GetProperty("productId").GetString() == SearchProductId);
        Assert.Contains(brand, item => item.GetProperty("kind").GetString() == "BRAND"
            && item.GetProperty("label").GetString() == "YONEX Nhật Bản");
        Assert.Contains(category, item => item.GetProperty("kind").GetString() == "CATEGORY"
            && item.GetProperty("label").GetString() == "Vợt Cầu Lông");

        var dedupeKeys = exact.Select(item =>
            $"{item.GetProperty("kind").GetString()}|{item.GetProperty("label").GetString()}|{item.GetProperty("productId")}").ToArray();
        Assert.Equal(dedupeKeys.Length, dedupeKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(exact, item => Assert.Contains(item.GetProperty("kind").GetString(),
            new[] { "PRODUCT", "SKU", "BRAND", "CATEGORY" }));
    }

    [Fact]
    public async Task Suggestions_enforce_limit_cache_and_short_query_contract()
    {
        await EnsureSearchCatalogAsync();

        using var limitedResponse = await fixture.Client.GetAsync(
            "/api/v1/catalog/search/suggestions?q=" + Uri.EscapeDataString("vot cau long") + "&limit=2");
        using var shortResponse = await fixture.Client.GetAsync("/api/v1/catalog/search/suggestions?q=a");
        using var invalidResponse = await fixture.Client.GetAsync("/api/v1/catalog/search/suggestions?q=yonex&limit=11");

        Assert.Equal(HttpStatusCode.OK, limitedResponse.StatusCode);
        Assert.Equal("public, max-age=30", limitedResponse.Headers.CacheControl?.ToString());
        Assert.True((await limitedResponse.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength() <= 2);
        Assert.Equal(HttpStatusCode.OK, shortResponse.StatusCode);
        Assert.Empty((await shortResponse.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
        var invalidBody = await invalidResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_ERROR", invalidBody.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("draft-only-needle")]
    [InlineData("suspended-only-needle")]
    [InlineData("archived-only-needle")]
    [InlineData("hidden-category-needle-971")]
    [InlineData("inactive-brand-needle-862")]
    [InlineData("zzqx-secret-917")]
    public async Task Suggestions_never_leak_non_public_data(string query)
    {
        await EnsureSearchCatalogAsync();

        Assert.Empty(await SuggestionsAsync(query, 10));
    }

    private async Task<IReadOnlyList<string>> ReadPageIdsAsync(string query, int firstPage, int pageCount)
    {
        var ids = new List<string>();
        for (var page = firstPage; page < firstPage + pageCount; page++)
        {
            var body = await SearchAsync(query, page, 1);
            ids.Add(body.GetProperty("items")[0].GetProperty("id").GetString()!);
        }
        return ids;
    }

    private async Task<JsonElement> SearchAsync(string query, int page = 1, int pageSize = 12)
    {
        var path = $"/api/v1/catalog/products?q={Uri.EscapeDataString(query)}&page={page}&pageSize={pageSize}";
        using var response = await fixture.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement[]> SuggestionsAsync(string query, int limit)
    {
        var path = $"/api/v1/catalog/search/suggestions?q={Uri.EscapeDataString(query)}&limit={limit}";
        using var response = await fixture.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToArray();
    }

    private static bool IsSearchProduct(JsonElement item) =>
        item.GetProperty("id").GetString() == SearchProductId;

    private async Task EnsureSearchCatalogAsync()
    {
        await using var connection = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO store.categories(id, name, slug, is_visible) VALUES
                ('50000000-0000-0000-0000-000000000001', 'Vợt Cầu Lông', 't50-vot-cau-long', true),
                ('50000000-0000-0000-0000-000000000002', 'Hidden Category Needle 971', 't50-hidden-category', false)
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO store.brands(id, name, slug, is_active) VALUES
                ('50000000-0000-0000-0000-000000000003', 'YONEX Nhật Bản', 't50-yonex', true),
                ('50000000-0000-0000-0000-000000000004', 'Inactive Brand Needle 862', 't50-inactive-brand', false)
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO store.products(
                id, title, publication_status, category_id, brand_id, created_by, created_at) VALUES
                ('50000000-0000-0000-0000-000000000010', 'Vợt Cầu Lông Yonex Astrox 88D Pro', 'PUBLISHED',
                 '50000000-0000-0000-0000-000000000001', '50000000-0000-0000-0000-000000000003',
                 '00000000-0000-0000-0000-000000000002', '2026-06-01Z'),
                ('50000000-0000-0000-0000-000000000011', 'Giày thi đấu chuyên nghiệp', 'PUBLISHED',
                 '50000000-0000-0000-0000-000000000001', '50000000-0000-0000-0000-000000000003',
                 '00000000-0000-0000-0000-000000000002', '2026-06-01Z'),
                ('50000000-0000-0000-0000-000000000012', 'Draft Only Needle', 'DRAFT', null, null,
                 '00000000-0000-0000-0000-000000000002', '2026-06-01Z'),
                ('50000000-0000-0000-0000-000000000013', 'Suspended Only Needle', 'SUSPENDED', null, null,
                 '00000000-0000-0000-0000-000000000002', '2026-06-01Z'),
                ('50000000-0000-0000-0000-000000000014', 'Archived Only Needle', 'ARCHIVED', null, null,
                 '00000000-0000-0000-0000-000000000002', '2026-06-01Z'),
                ('50000000-0000-0000-0000-000000000015', 'Opaque Public Item One', 'PUBLISHED',
                 '50000000-0000-0000-0000-000000000002', null,
                 '00000000-0000-0000-0000-000000000002', '2026-06-01Z'),
                ('50000000-0000-0000-0000-000000000016', 'Opaque Public Item Two', 'PUBLISHED', null,
                 '50000000-0000-0000-0000-000000000004',
                 '00000000-0000-0000-0000-000000000002', '2026-06-01Z'),
                ('50000000-0000-0000-0000-000000000017', 'Opaque Public Item Three', 'PUBLISHED', null, null,
                 '00000000-0000-0000-0000-000000000002', '2026-06-01Z'),
                ('50000000-0000-0000-0000-000000000018', 'AX88D-PRO-4U', 'PUBLISHED', null, null,
                 '00000000-0000-0000-0000-000000000002', '2026-06-01Z'),
                ('50000000-0000-0000-0000-000000000020', 'Shared Ranking Racket', 'PUBLISHED', null, null,
                 '00000000-0000-0000-0000-000000000002', '2026-05-01Z'),
                ('50000000-0000-0000-0000-000000000021', 'Shared Ranking Racket', 'PUBLISHED', null, null,
                 '00000000-0000-0000-0000-000000000002', '2026-05-01Z'),
                ('50000000-0000-0000-0000-000000000022', 'Shared Ranking Racket', 'PUBLISHED', null, null,
                 '00000000-0000-0000-0000-000000000002', '2026-05-01Z')
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO store.product_skus(id, product_id, sku_code, option_label, regular_price, sku_status) VALUES
                ('50000000-0000-0000-0000-000000000100', '50000000-0000-0000-0000-000000000010', 'AX88D-PRO-4U', '4U / G5 Đỏ', 3490000, 'ACTIVE'),
                ('50000000-0000-0000-0000-000000000101', '50000000-0000-0000-0000-000000000010', 'AX88D-PRO-3U', '3U / G5 Xanh', 3590000, 'ACTIVE'),
                ('50000000-0000-0000-0000-000000000102', '50000000-0000-0000-0000-000000000011', 'SHOE-PRO', 'Size 42', 1890000, 'ACTIVE'),
                ('50000000-0000-0000-0000-000000000103', '50000000-0000-0000-0000-000000000012', 'DRAFT-T50', 'Default', 100, 'ACTIVE'),
                ('50000000-0000-0000-0000-000000000104', '50000000-0000-0000-0000-000000000013', 'SUSPENDED-T50', 'Default', 100, 'ACTIVE'),
                ('50000000-0000-0000-0000-000000000105', '50000000-0000-0000-0000-000000000014', 'ARCHIVED-T50', 'Default', 100, 'ACTIVE'),
                ('50000000-0000-0000-0000-000000000106', '50000000-0000-0000-0000-000000000015', 'OPAQUE-LIVE-ONE', 'Default', 100, 'ACTIVE'),
                ('50000000-0000-0000-0000-000000000107', '50000000-0000-0000-0000-000000000016', 'OPAQUE-LIVE-TWO', 'Default', 100, 'ACTIVE'),
                ('50000000-0000-0000-0000-000000000108', '50000000-0000-0000-0000-000000000017', 'ZZQX-SECRET-917', 'Secret option', 100, 'INACTIVE'),
                ('50000000-0000-0000-0000-000000000109', '50000000-0000-0000-0000-000000000017', 'LIVE-ACCESSORY-204', 'Default', 100, 'ACTIVE'),
                ('50000000-0000-0000-0000-000000000110', '50000000-0000-0000-0000-000000000018', 'TITLE-COMPETITOR', 'Default', 100, 'ACTIVE'),
                ('50000000-0000-0000-0000-000000000120', '50000000-0000-0000-0000-000000000020', 'SHARED-20', 'Default', 100, 'ACTIVE'),
                ('50000000-0000-0000-0000-000000000121', '50000000-0000-0000-0000-000000000021', 'SHARED-21', 'Default', 100, 'ACTIVE'),
                ('50000000-0000-0000-0000-000000000122', '50000000-0000-0000-0000-000000000022', 'SHARED-22', 'Default', 100, 'ACTIVE')
            ON CONFLICT (id) DO NOTHING;
            """, connection);
        await command.ExecuteNonQueryAsync();
    }
}
