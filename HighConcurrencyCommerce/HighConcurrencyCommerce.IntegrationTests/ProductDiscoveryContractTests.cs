using System.ComponentModel.DataAnnotations;
using HighConcurrencyCommerce.Application.Catalog;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class ProductDiscoveryContractTests
{
    [Fact]
    public void Related_query_defaults_to_six_and_accepts_valid_range()
    {
        var defaultQuery = new RelatedProductsQuery();
        Assert.Equal(6, defaultQuery.Limit);
        Assert.Empty(Validate(defaultQuery));

        Assert.Empty(Validate(new RelatedProductsQuery { Limit = 1 }));
        Assert.Empty(Validate(new RelatedProductsQuery { Limit = 12 }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(13)]
    [InlineData(100)]
    public void Related_query_rejects_out_of_range_limit(int invalidLimit)
    {
        var query = new RelatedProductsQuery { Limit = invalidLimit };
        var results = Validate(query);
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(RelatedProductsQuery.Limit)));
    }

    [Fact]
    public void Batch_query_accepts_between_one_and_twelve_unique_product_ids()
    {
        Assert.Empty(Validate(new ProductBatchQuery { Id = [Guid.NewGuid()] }));

        var twelveIds = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToArray();
        Assert.Empty(Validate(new ProductBatchQuery { Id = twelveIds }));
    }

    [Fact]
    public void Batch_query_rejects_empty_and_more_than_twelve_product_ids()
    {
        Assert.Contains(Validate(new ProductBatchQuery()), result => result.MemberNames.Contains(nameof(ProductBatchQuery.Id)));

        var thirteenIds = Enumerable.Range(0, 13).Select(_ => Guid.NewGuid()).ToArray();
        Assert.Contains(Validate(new ProductBatchQuery { Id = thirteenIds }),
            result => result.ErrorMessage?.Contains("12", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Batch_query_rejects_duplicate_and_empty_product_ids()
    {
        var duplicate = Guid.NewGuid();
        Assert.Contains(Validate(new ProductBatchQuery { Id = [duplicate, duplicate] }),
            result => result.ErrorMessage?.Contains("trùng lặp", StringComparison.Ordinal) == true);

        Assert.Contains(Validate(new ProductBatchQuery { Id = [Guid.Empty] }),
            result => result.ErrorMessage?.Contains("không hợp lệ", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Response_contracts_preserve_item_order_and_counts()
    {
        var item1 = Summary(Guid.NewGuid(), "Vợt A");
        var item2 = Summary(Guid.NewGuid(), "Vợt B");

        var sourceId = Guid.NewGuid();
        var related = new RelatedProductsDto(sourceId, [item1, item2]);
        Assert.Equal(sourceId, related.SourceProductId);
        Assert.Equal([item1.Id, item2.Id], related.Items.Select(i => i.Id));

        var batch = new ProductSummaryBatchDto([item1, item2], RequestedCount: 3, OmittedCount: 1);
        Assert.Equal([item1.Id, item2.Id], batch.Items.Select(i => i.Id));
        Assert.Equal(3, batch.RequestedCount);
        Assert.Equal(1, batch.OmittedCount);
    }

    private static IReadOnlyList<ValidationResult> Validate(object query)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(query, new ValidationContext(query), results, validateAllProperties: true);
        return results;
    }

    private static ProductSummaryDto Summary(Guid id, string title) => new(
        id,
        title,
        new CategoryDto(Guid.NewGuid(), "Vợt cầu lông", "vot-cau-long"),
        new BrandDto(Guid.NewGuid(), "Yonex", "yonex", "Nhật Bản", null),
        MinPrice: 2000000m,
        InStock: true,
        ImageUrl: null,
        AverageRating: 4.8m,
        ReviewCount: 10);
}

