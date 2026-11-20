using HighConcurrencyCommerce.Application.Shipping;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class ShippingFeeCalculatorTests
{
    private readonly ShippingFeeCalculator calculator = new();

    [Theory]
    [InlineData(999_999, 30_000, 5_000, "1000000", 35_000, false)]
    [InlineData(1_000_000, 30_000, 5_000, "1000000", 0, true)]
    [InlineData(1_000_001, 30_000, 5_000, "1000000", 0, true)]
    [InlineData(1_000_000, 30_000, 5_000, null, 35_000, false)]
    [InlineData(0, 0, 0, null, 0, false)]
    public void Calculate_applies_surcharge_and_free_threshold_at_exact_boundary(
        decimal subtotal,
        decimal baseFee,
        decimal surcharge,
        string? threshold,
        decimal expectedFee,
        bool expectedFree)
    {
        var result = calculator.Calculate(new ShippingFeeCalculationInput(
            subtotal, baseFee, surcharge, Parse(threshold)));

        Assert.Equal(baseFee + surcharge, result.FeeBeforeFreeShipping);
        Assert.Equal(expectedFee, result.ShippingFee);
        Assert.Equal(expectedFree, result.FreeShippingApplied);
    }

    [Theory]
    [InlineData(-1, 0, 0, null)]
    [InlineData(1, -1, 0, null)]
    [InlineData(1, 0, -1, null)]
    [InlineData(1.5, 0, 0, null)]
    [InlineData(1, 0, 0, "0")]
    [InlineData(1, 0, 0, "10.5")]
    public void Calculate_rejects_invalid_money_values(
        decimal subtotal,
        decimal baseFee,
        decimal surcharge,
        string? threshold)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => calculator.Calculate(
            new ShippingFeeCalculationInput(subtotal, baseFee, surcharge, Parse(threshold))));
    }

    [Theory]
    [InlineData("Thành phố Hồ Chí Minh", "HO CHI MINH")]
    [InlineData(" TP. Hồ   Chí-Minh ", "HO CHI MINH")]
    [InlineData("Tỉnh Đắk Lắk", "DAK LAK")]
    [InlineData("Bà Rịa – Vũng Tàu", "BA RIA VUNG TAU")]
    public void Province_normalizer_is_diacritic_case_prefix_and_spacing_insensitive(
        string province,
        string expected)
    {
        Assert.Equal(expected, new ProvinceKeyNormalizer().Normalize(province));
    }

    private static decimal? Parse(string? value) => value is null
        ? null
        : decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
