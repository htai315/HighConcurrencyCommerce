using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class CatalogFacetTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string DisplayCategoryId = "10000000-0000-0000-0000-000000000001";
    private const string BrandAId = "60000000-0000-0000-0000-000000000001";
    private const string AlphaProductId = "20000000-0000-0000-0000-000000000001";

    [Fact]
    public async Task Category_facet_excludes_its_own_filter_but_other_facets_preserve_it()
    {
        await EnsureFacetDataAsync();

        var body = await GetProductsAsync($"categoryId={DisplayCategoryId}");
        var facets = body.GetProperty("facets");

        Assert.Equal(1, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(AlphaProductId, body.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal(new[] { "Display:1", "Workspace:1" }, ReadFacetPairs(facets.GetProperty("categories")));
        Assert.Equal(new[] { "AeroSport:1" }, ReadFacetPairs(facets.GetProperty("brands")));
        Assert.Equal(1, facets.GetProperty("inStockCount").GetInt32());
        Assert.Equal(2500000m, facets.GetProperty("priceMin").GetDecimal());
        Assert.Equal(3500000m, facets.GetProperty("priceMax").GetDecimal());
    }

    [Fact]
    public async Task Brand_facet_excludes_its_own_filter_and_hides_non_public_categories()
    {
        await EnsureFacetDataAsync();

        var body = await GetProductsAsync($"brandId={BrandAId}");
        var facets = body.GetProperty("facets");

        Assert.Equal(3, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(new[] { "Display:1" }, ReadFacetPairs(facets.GetProperty("categories")));
        Assert.Equal(new[] { "AeroSport:3", "BirdiePro:1" }, ReadFacetPairs(facets.GetProperty("brands")));
        Assert.Equal(2, facets.GetProperty("inStockCount").GetInt32());
        Assert.Equal(100000m, facets.GetProperty("priceMin").GetDecimal());
        Assert.Equal(3500000m, facets.GetProperty("priceMax").GetDecimal());
        Assert.DoesNotContain("Private category", facets.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Price_and_stock_facets_exclude_price_and_stock_filters()
    {
        await EnsureFacetDataAsync();

        var body = await GetProductsAsync("minPrice=3000000&maxPrice=8000000&inStock=true");
        var facets = body.GetProperty("facets");

        Assert.Equal(1, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(AlphaProductId, body.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal(new[] { "Display:1" }, ReadFacetPairs(facets.GetProperty("categories")));
        Assert.Equal(new[] { "AeroSport:1" }, ReadFacetPairs(facets.GetProperty("brands")));
        Assert.Equal(2, facets.GetProperty("inStockCount").GetInt32());
        Assert.Equal(100000m, facets.GetProperty("priceMin").GetDecimal());
        Assert.Equal(7000000m, facets.GetProperty("priceMax").GetDecimal());
    }

    [Fact]
    public async Task Rating_filter_and_sort_use_only_public_non_deleted_reviews()
    {
        await EnsureFacetDataAsync();

        var filtered = await GetProductsAsync("minRating=4");
        var sorted = await GetProductsAsync("sort=rating-desc");

        Assert.Equal(1, filtered.GetProperty("totalCount").GetInt32());
        Assert.Equal(AlphaProductId, filtered.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal(5m, filtered.GetProperty("items")[0].GetProperty("averageRating").GetDecimal());
        Assert.Equal(2500000m, filtered.GetProperty("facets").GetProperty("priceMin").GetDecimal());
        Assert.Equal(AlphaProductId, sorted.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal("Beta Keyboard", sorted.GetProperty("items")[1].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Best_selling_uses_only_recent_completed_delivered_quantities()
    {
        await EnsureFacetDataAsync();

        var sorted = await GetProductsAsync("sort=best-selling");
        var items = sorted.GetProperty("items");

        Assert.Equal("Cable 100%_Pro", items[0].GetProperty("title").GetString());
        Assert.Equal("Alpha Monitor", items[1].GetProperty("title").GetString());
    }

    private async Task<JsonElement> GetProductsAsync(string query)
    {
        using var response = await fixture.Client.GetAsync("/api/v1/catalog/products?" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string[] ReadFacetPairs(JsonElement facets) => facets.EnumerateArray()
        .Select(item => $"{item.GetProperty("name").GetString()}:{item.GetProperty("count").GetInt32()}")
        .ToArray();

    private async Task EnsureFacetDataAsync()
    {
        await using var connection = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO store.brands(id, name, slug, is_active) VALUES
                ('60000000-0000-0000-0000-000000000001', 'AeroSport', 'facet-aerosport', true),
                ('60000000-0000-0000-0000-000000000002', 'BirdiePro', 'facet-birdiepro', true)
            ON CONFLICT (id) DO NOTHING;

            UPDATE store.products SET brand_id = '60000000-0000-0000-0000-000000000001'
            WHERE id IN (
                '20000000-0000-0000-0000-000000000001',
                '20000000-0000-0000-0000-000000000003',
                '20000000-0000-0000-0000-000000000007');
            UPDATE store.products SET brand_id = '60000000-0000-0000-0000-000000000002'
            WHERE id = '20000000-0000-0000-0000-000000000002';

            INSERT INTO store.sales_orders(
                id, customer_id, order_number, payment_method, order_status,
                shipping_address_snapshot, subtotal_amount, voucher_discount_amount,
                shipping_fee, shipping_method_code_snapshot, shipping_method_name_snapshot,
                total_amount, deadline_at, confirmed_at)
            VALUES(
                '60000000-0000-0000-0000-000000000010',
                '00000000-0000-0000-0000-000000000002',
                'HC-T50-FACET-REVIEWS', 'COD', 'COMPLETED', '{}',
                9500000, 0, 0, 'LEGACY_FREE', 'Miễn phí', 9500000,
                now() + interval '1 day', now())
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO store.sales_order_items(
                id, order_id, product_sku_id, product_title_snapshot,
                sku_code_snapshot, sku_option_label_snapshot, quantity, unit_price)
            VALUES
                ('60000000-0000-0000-0000-000000000011', '60000000-0000-0000-0000-000000000010',
                 '30000000-0000-0000-0000-000000000001', 'Alpha Monitor', 'ALPHA-A', 'Standard', 1, 2500000),
                ('60000000-0000-0000-0000-000000000012', '60000000-0000-0000-0000-000000000010',
                 '30000000-0000-0000-0000-000000000004', 'Beta Keyboard', 'BETA', 'Default', 1, 7000000)
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO store.product_reviews(
                id, customer_id, product_id, qualifying_order_item_id, rating, content, review_status,
                moderated_by, moderated_at, moderation_reason, deleted_at)
            VALUES
                ('60000000-0000-0000-0000-000000000013', '00000000-0000-0000-0000-000000000002',
                 '20000000-0000-0000-0000-000000000001', '60000000-0000-0000-0000-000000000011',
                 5, 'Sản phẩm Alpha chất lượng rất tốt.', 'PUBLISHED', null, null, null, null),
                ('60000000-0000-0000-0000-000000000014', '00000000-0000-0000-0000-000000000002',
                 '20000000-0000-0000-0000-000000000002', '60000000-0000-0000-0000-000000000012',
                 3, 'Sản phẩm Beta ở mức khá ổn.', 'PUBLISHED', null, null, null, null),
                ('60000000-0000-0000-0000-000000000015', '00000000-0000-0000-0000-000000000002',
                 '20000000-0000-0000-0000-000000000001', '60000000-0000-0000-0000-000000000011',
                 1, 'Review đã xóa không được tính điểm.', 'PUBLISHED', null, null, null, now())
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO store.sales_orders(
                id, customer_id, order_number, payment_method, order_status,
                shipping_address_snapshot, subtotal_amount, voucher_discount_amount,
                shipping_fee, shipping_method_code_snapshot, shipping_method_name_snapshot,
                total_amount, deadline_at, confirmed_at)
            VALUES
                ('60000000-0000-0000-0000-000000000020', '00000000-0000-0000-0000-000000000002',
                 'HC-T50-RECENT-SALES', 'COD', 'COMPLETED', '{}', 5500000, 0, 0,
                 'LEGACY_FREE', 'Miễn phí', 5500000, now()+interval '1 day', now()),
                ('60000000-0000-0000-0000-000000000021', '00000000-0000-0000-0000-000000000002',
                 'HC-T50-OLD-SALES', 'COD', 'COMPLETED', '{}', 247500000, 0, 0,
                 'LEGACY_FREE', 'Miễn phí', 247500000, now()+interval '1 day', now()),
                ('60000000-0000-0000-0000-000000000022', '00000000-0000-0000-0000-000000000002',
                 'HC-T50-NOT-DELIVERED', 'COD', 'COMPLETED', '{}', 693000000, 0, 0,
                 'LEGACY_FREE', 'Miễn phí', 693000000, now()+interval '1 day', now())
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO store.sales_order_items(
                id, order_id, product_sku_id, product_title_snapshot,
                sku_code_snapshot, sku_option_label_snapshot, quantity, unit_price)
            VALUES
                ('60000000-0000-0000-0000-000000000030', '60000000-0000-0000-0000-000000000020',
                 '30000000-0000-0000-0000-000000000009', 'Cable 100%_Pro', 'CABLE', 'Default', 5, 100000),
                ('60000000-0000-0000-0000-000000000031', '60000000-0000-0000-0000-000000000020',
                 '30000000-0000-0000-0000-000000000001', 'Alpha Monitor', 'ALPHA-A', 'Standard', 2, 250000),
                ('60000000-0000-0000-0000-000000000032', '60000000-0000-0000-0000-000000000021',
                 '30000000-0000-0000-0000-000000000001', 'Alpha Monitor', 'ALPHA-A', 'Standard', 99, 2500000),
                ('60000000-0000-0000-0000-000000000033', '60000000-0000-0000-0000-000000000022',
                 '30000000-0000-0000-0000-000000000004', 'Beta Keyboard', 'BETA', 'Default', 99, 7000000)
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO store.shipments(id, order_id, shipment_status, shipped_at, delivered_at) VALUES
                ('60000000-0000-0000-0000-000000000040', '60000000-0000-0000-0000-000000000020',
                 'DELIVERED', now()-interval '2 days', now()-interval '1 day'),
                ('60000000-0000-0000-0000-000000000041', '60000000-0000-0000-0000-000000000021',
                 'DELIVERED', now()-interval '101 days', now()-interval '100 days'),
                ('60000000-0000-0000-0000-000000000042', '60000000-0000-0000-0000-000000000022',
                 'SHIPPING', now()-interval '1 day', null)
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO store.product_reviews(
                id, customer_id, product_id, qualifying_order_item_id, rating, content, review_status,
                moderated_by, moderated_at, moderation_reason)
            VALUES(
                '60000000-0000-0000-0000-000000000043', '00000000-0000-0000-0000-000000000002',
                '20000000-0000-0000-0000-000000000007', '60000000-0000-0000-0000-000000000030',
                5, 'Review ẩn không được dùng trong rating.', 'HIDDEN',
                '00000000-0000-0000-0000-000000000002', now(), 'Fixture kiểm duyệt')
            ON CONFLICT (id) DO NOTHING;
            """, connection);
        await command.ExecuteNonQueryAsync();
    }
}
