using System.ComponentModel.DataAnnotations;

namespace HighConcurrencyCommerce.Application.Wishlist;

public sealed class UpdateWishlistAlertSettingsRequest
{
    public bool BackInStockEnabled { get; init; }
    public bool PriceDropEnabled { get; init; }
    public bool EmailEnabled { get; init; }

    [Range(0, int.MaxValue, ErrorMessage = "Phiên bản cấu hình cảnh báo không hợp lệ.")]
    public int ExpectedRevision { get; init; }
}

public sealed record WishlistAlertSummaryDto(
    Guid ProductId,
    bool BackInStockEnabled,
    bool PriceDropEnabled,
    bool EmailEnabled,
    int Revision);

public sealed record WishlistAlertSettingsDto(
    Guid ProductId,
    bool BackInStockEnabled,
    bool PriceDropEnabled,
    bool EmailEnabled,
    string CurrentAvailability,
    decimal? CurrentMinPrice,
    int Revision,
    DateTime UpdatedAt);

public interface IWishlistAlertService
{
    Task<WishlistAlertSettingsDto> GetSettingsAsync(
        Guid customerId,
        Guid productId,
        CancellationToken ct = default);

    Task<WishlistAlertSettingsDto> UpdateSettingsAsync(
        Guid customerId,
        Guid productId,
        UpdateWishlistAlertSettingsRequest request,
        CancellationToken ct = default);
}
