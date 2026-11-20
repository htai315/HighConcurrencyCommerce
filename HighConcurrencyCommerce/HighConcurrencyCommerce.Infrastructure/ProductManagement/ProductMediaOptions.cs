namespace HighConcurrencyCommerce.Infrastructure.ProductManagement;

public sealed class ProductMediaOptions
{
    public string RootPath { get; set; } = "product-media";
    public long MaxFileBytes { get; set; } = 8 * 1024 * 1024;
}
