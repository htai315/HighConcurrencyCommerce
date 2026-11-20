using System;
using System.Collections.Generic;
using HighConcurrencyCommerce.Application.Catalog;

namespace HighConcurrencyCommerce.Application.Wishlist;

public static class WishlistAvailability
{
    public const string Available = "AVAILABLE";
    public const string OutOfStock = "OUT_OF_STOCK";
    public const string ProductUnavailable = "PRODUCT_UNAVAILABLE";
}

public sealed record WishlistItemDto(
    Guid ProductId,
    DateTime AddedAt,
    string Availability,
    ProductSummaryDto? Product,
    WishlistAlertSummaryDto Alerts);

public sealed record WishlistPageDto(
    IReadOnlyList<WishlistItemDto> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record WishlistSummaryDto(
    IReadOnlyList<Guid> ProductIds,
    int TotalCount,
    IReadOnlyList<WishlistAlertSummaryDto> Alerts);

public sealed class WishlistFailure : Exception
{
    public string Code { get; }
    public int Status { get; }
    public int? CurrentRevision { get; }

    public WishlistFailure(
        string code,
        string message,
        int status = 400,
        int? currentRevision = null) : base(message)
    {
        Code = code;
        Status = status;
        CurrentRevision = currentRevision;
    }
}

public interface IWishlistService
{
    Task<WishlistPageDto> GetWishlistAsync(Guid customerId, int page = 1, int pageSize = 12, CancellationToken cancellationToken = default);
    Task<WishlistSummaryDto> GetSummaryAsync(Guid customerId, CancellationToken cancellationToken = default);
    Task AddItemAsync(Guid customerId, Guid productId, CancellationToken cancellationToken = default);
    Task RemoveItemAsync(Guid customerId, Guid productId, CancellationToken cancellationToken = default);
}

