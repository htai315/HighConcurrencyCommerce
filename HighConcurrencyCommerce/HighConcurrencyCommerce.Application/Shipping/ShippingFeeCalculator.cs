namespace HighConcurrencyCommerce.Application.Shipping;

public sealed class ShippingFeeCalculator : IShippingFeeCalculator
{
    public ShippingFeeCalculationResult Calculate(ShippingFeeCalculationInput input)
    {
        EnsureWholeNonNegative(input.MerchandiseSubtotal, nameof(input.MerchandiseSubtotal));
        EnsureWholeNonNegative(input.BaseFee, nameof(input.BaseFee));
        EnsureWholeNonNegative(input.ProvinceSurcharge, nameof(input.ProvinceSurcharge));

        if (input.FreeShippingThreshold is { } threshold)
        {
            if (threshold <= 0 || threshold != decimal.Floor(threshold))
                throw new ArgumentOutOfRangeException(nameof(input.FreeShippingThreshold),
                    "Ngưỡng miễn phí phải là số VND nguyên dương.");
        }

        var feeBeforeFreeShipping = checked(input.BaseFee + input.ProvinceSurcharge);
        var freeShippingApplied = input.FreeShippingThreshold is { } freeThreshold
            && input.MerchandiseSubtotal >= freeThreshold;

        return new ShippingFeeCalculationResult(
            freeShippingApplied ? 0m : feeBeforeFreeShipping,
            feeBeforeFreeShipping,
            freeShippingApplied);
    }

    private static void EnsureWholeNonNegative(decimal value, string parameterName)
    {
        if (value < 0 || value != decimal.Floor(value))
            throw new ArgumentOutOfRangeException(parameterName, "Giá trị tiền phải là số VND nguyên không âm.");
    }
}
