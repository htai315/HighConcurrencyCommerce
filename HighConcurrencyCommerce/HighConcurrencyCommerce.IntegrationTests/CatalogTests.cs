using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class CatalogTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string ProductId = "20000000-0000-0000-0000-000000000001";
    private const string CategoryId = "10000000-0000-0000-0000-000000000001";

    private async Task<JsonElement> GetAsync(string path)
    {
        using var response = await fixture.Client.GetAsync("/api/v1/catalog/" + path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Categories_return_only_visible_entries()
    {
        var body = await GetAsync("categories");
        Assert.Equal(new[] { "Display", "Workspace" }, body.EnumerateArray().Select(c => c.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Products_hide_drafts_archives_and_products_without_active_skus()
    {
        var body = await GetAsync("products");
        Assert.Equal(4, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, body.GetProperty("page").GetInt32());
        var items = body.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(new[] { "Alpha Monitor", "Beta Keyboard", "Gamma Headphones", "Cable 100%_Pro" }, items.Select(p => p.GetProperty("title").GetString()));
        Assert.Equal(2500000m, items[0].GetProperty("minPrice").GetDecimal());
        Assert.True(items[0].GetProperty("inStock").GetBoolean());
        Assert.False(items[1].GetProperty("inStock").GetBoolean());
        Assert.False(items[2].GetProperty("inStock").GetBoolean());
        Assert.Equal(JsonValueKind.Null, items[2].GetProperty("category").ValueKind);
        Assert.DoesNotContain("createdBy", body.GetRawText());
        Assert.DoesNotContain("reservedQty", body.GetRawText());
    }

    [Theory]
    [InlineData("alpha", "Alpha Monitor")]
    [InlineData("ALPHA", "Alpha Monitor")]
    [InlineData("  alpha  ", "Alpha Monitor")]
    [InlineData("%", "Cable 100%_Pro")]
    [InlineData("_", "Cable 100%_Pro")]
    public async Task Search_is_case_insensitive_and_wildcards_are_literal(string query, string expected)
    {
        var body = await GetAsync("products?q=" + Uri.EscapeDataString(query));
        Assert.Equal(1, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(expected, body.GetProperty("items")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Filters_and_paging_return_stable_results()
    {
        var filtered = await GetAsync($"products?categoryId={CategoryId}&minPrice=3000000&maxPrice=4000000");
        Assert.Equal(1, filtered.GetProperty("totalCount").GetInt32());
        Assert.Equal(ProductId, filtered.GetProperty("items")[0].GetProperty("id").GetString());
        // Two different SKUs straddling a range must not falsely match that range.
        var gap = await GetAsync($"products?categoryId={CategoryId}&minPrice=2700000&maxPrice=3000000");
        Assert.Equal(0, gap.GetProperty("totalCount").GetInt32());
        var page = await GetAsync("products?sort=price-asc&page=2&pageSize=1");
        Assert.Equal(4, page.GetProperty("totalCount").GetInt32());
        Assert.Equal("Gamma Headphones", page.GetProperty("items")[0].GetProperty("title").GetString());
        var descending = await GetAsync("products?sort=price-desc&pageSize=1");
        Assert.Equal("Beta Keyboard", descending.GetProperty("items")[0].GetProperty("title").GetString());
        var outside = await GetAsync("products?page=999&pageSize=1");
        Assert.Empty(outside.GetProperty("items").EnumerateArray());
        var hiddenCategory = await GetAsync("products?categoryId=10000000-0000-0000-0000-000000000003");
        Assert.Equal(0, hiddenCategory.GetProperty("totalCount").GetInt32());
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=2147483647")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("page=invalid")]
    [InlineData("categoryId=invalid")]
    [InlineData("minPrice=-1")]
    [InlineData("minPrice=200&maxPrice=100")]
    [InlineData("sort=random")]
    [InlineData("q=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Invalid_queries_return_validation_problem(string query)
    {
        using var response = await fixture.Client.GetAsync("/api/v1/catalog/products?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_ERROR", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("correlationId").GetString()));
    }

    [Fact]
    public async Task Detail_excludes_inactive_skus_and_uses_available_stock()
    {
        var body = await GetAsync("products/" + ProductId);
        Assert.Equal("Alpha Monitor", body.GetProperty("title").GetString());
        var skus = body.GetProperty("skus").EnumerateArray().ToArray();
        Assert.Equal(2, skus.Length);
        Assert.False(skus[0].GetProperty("inStock").GetBoolean());
        Assert.True(skus[1].GetProperty("inStock").GetBoolean());
        Assert.DoesNotContain("INACTIVE-SECRET", body.GetRawText());
        Assert.DoesNotContain("password", body.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("physicalQty", body.GetRawText());
    }

    [Theory]
    [InlineData("20000000-0000-0000-0000-000000000004")]
    [InlineData("20000000-0000-0000-0000-000000000005")]
    [InlineData("20000000-0000-0000-0000-000000000006")]
    [InlineData("99999999-0000-0000-0000-000000000000")]
    public async Task Non_public_product_detail_returns_404(string id)
    {
        using var response = await fixture.Client.GetAsync("/api/v1/catalog/products/" + id);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NOT_FOUND", body.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("' OR 1=1 --")]
    [InlineData("\\")]
    public async Task Search_treats_sql_and_escape_characters_as_data(string query)
    {
        var body = await GetAsync("products?q=" + Uri.EscapeDataString(query));
        Assert.Equal(0, body.GetProperty("totalCount").GetInt32());
    }
}
