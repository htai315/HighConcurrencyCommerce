using System.ComponentModel.DataAnnotations;
using HighConcurrencyCommerce.Application.Catalog;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class CatalogContractTests
{
    [Theory]
    [InlineData("  Vợt   CẦU\tLông Đỏ  ", "vot cau long do")]
    [InlineData("YONEX ÁSTRÓX 88D", "yonex astrox 88d")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Search_normalizer_is_accent_case_and_whitespace_insensitive(string? input, string? expected) =>
        Assert.Equal(expected, CatalogSearchNormalizer.NormalizeText(input));

    [Fact]
    public void Catalog_query_chooses_a_deterministic_effective_sort()
    {
        var search = CatalogSearchNormalizer.Normalize(new CatalogQuery { Q = "  Vợt Yonex " });
        var browse = CatalogSearchNormalizer.Normalize(new CatalogQuery());
        var explicitSort = CatalogSearchNormalizer.Normalize(new CatalogQuery
        {
            Q = "Yonex",
            Sort = CatalogSort.PriceAscending
        });

        Assert.Equal("vot yonex", search.Text);
        Assert.Equal(CatalogSort.Relevance, search.Sort);
        Assert.Null(browse.Text);
        Assert.Equal(CatalogSort.Newest, browse.Sort);
        Assert.Equal(CatalogSort.PriceAscending, explicitSort.Sort);
    }

    [Theory]
    [InlineData("relevance", null)]
    [InlineData("unknown", "yonex")]
    public void Catalog_query_rejects_invalid_sort_combinations(string sort, string? query)
    {
        var model = new CatalogQuery { Sort = sort, Q = query };
        var results = new List<ValidationResult>();

        Assert.False(Validator.TryValidateObject(model, new ValidationContext(model), results, true));
        Assert.NotEmpty(results);
    }

    [Fact]
    public void Suggestion_query_contract_enforces_public_limits()
    {
        var invalidText = new SearchSuggestionQuery { Q = new string('x', 81) };
        var textResults = new List<ValidationResult>();
        var invalidLimit = new SearchSuggestionQuery { Q = "yonex", Limit = 11 };
        var limitResults = new List<ValidationResult>();

        Assert.False(Validator.TryValidateObject(
            invalidText, new ValidationContext(invalidText), textResults, true));
        Assert.Contains(textResults, result => result.MemberNames.Contains(nameof(SearchSuggestionQuery.Q)));
        Assert.False(Validator.TryValidateObject(
            invalidLimit, new ValidationContext(invalidLimit), limitResults, true));
        Assert.Contains(limitResults, result => result.MemberNames.Contains(nameof(SearchSuggestionQuery.Limit)));
    }
}
