using System.ComponentModel.DataAnnotations;
using HighConcurrencyCommerce.Application.Catalog;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class ProductComparisonContractTests
{
    [Fact]
    public void Query_accepts_between_one_and_four_unique_product_ids()
    {
        Assert.Empty(Validate(new ProductComparisonQuery { Id = [Guid.NewGuid()] }));
        Assert.Empty(Validate(new ProductComparisonQuery
        {
            Id = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()]
        }));
    }

    [Fact]
    public void Query_rejects_empty_and_more_than_four_product_ids()
    {
        Assert.Contains(Validate(new ProductComparisonQuery()), result => result.MemberNames.Contains(nameof(ProductComparisonQuery.Id)));
        Assert.Contains(Validate(new ProductComparisonQuery
        {
            Id = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()]
        }), result => result.MemberNames.Contains(nameof(ProductComparisonQuery.Id)));
    }

    [Fact]
    public void Query_rejects_duplicate_and_empty_product_ids()
    {
        var duplicate = Guid.NewGuid();
        Assert.Contains(Validate(new ProductComparisonQuery { Id = [duplicate, duplicate] }),
            result => result.ErrorMessage?.Contains("trùng lặp", StringComparison.Ordinal) == true);
        Assert.Contains(Validate(new ProductComparisonQuery { Id = [Guid.Empty] }),
            result => result.ErrorMessage?.Contains("không hợp lệ", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Response_contract_preserves_item_order_and_explicit_omission_counts()
    {
        var first = Detail(Guid.NewGuid(), "Sản phẩm A");
        var second = Detail(Guid.NewGuid(), "Sản phẩm B");

        var response = new ProductComparisonDto([first, second], RequestedCount: 3, OmittedCount: 1);

        Assert.Equal([first.Id, second.Id], response.Items.Select(item => item.Id));
        Assert.Equal(3, response.RequestedCount);
        Assert.Equal(1, response.OmittedCount);
        Assert.Equal("COMPARISON_CATEGORY_MISMATCH", ProductComparisonErrorCodes.CategoryMismatch);
    }

    private static IReadOnlyList<ValidationResult> Validate(ProductComparisonQuery query)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(query, new ValidationContext(query), results, validateAllProperties: true);
        return results;
    }

    private static ProductDetailDto Detail(Guid id, string title) => new(
        id,
        title,
        null,
        null,
        null,
        [new SkuDto(Guid.NewGuid(), "SKU", "Tiêu chuẩn", 1m, true)],
        [],
        [],
        0m,
        0);
}
