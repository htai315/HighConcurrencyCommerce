namespace HighConcurrencyCommerce.Infrastructure.CarrierTracking;

public sealed class CarrierLabelOptions
{
    public string RootPath { get; set; } = "carrier-labels";
    public long MaxFileBytes { get; set; } = 5 * 1024 * 1024;
}
