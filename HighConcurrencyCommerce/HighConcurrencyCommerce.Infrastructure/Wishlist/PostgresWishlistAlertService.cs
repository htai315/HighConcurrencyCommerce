using System.Data;
using HighConcurrencyCommerce.Application.Wishlist;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace HighConcurrencyCommerce.Infrastructure.Wishlist;

public sealed class PostgresWishlistAlertService(
    StoreDbContext db,
    TimeProvider timeProvider) : IWishlistAlertService
{
    public async Task<WishlistAlertSettingsDto> GetSettingsAsync(
        Guid customerId,
        Guid productId,
        CancellationToken ct = default)
    {
        var item = await db.CustomerWishlistItems
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.CustomerId == customerId && x.ProductId == productId, ct)
            ?? throw NotFound();
        var state = await ReadProductStateAsync(productId, ct);
        return ToDto(item, state);
    }

    public async Task<WishlistAlertSettingsDto> UpdateSettingsAsync(
        Guid customerId,
        Guid productId,
        UpdateWishlistAlertSettingsRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EmailEnabled && !request.BackInStockEnabled && !request.PriceDropEnabled)
        {
            throw new WishlistFailure(
                "INVALID_ALERT_SETTINGS",
                "Kênh email chỉ có thể bật khi có ít nhất một loại cảnh báo.",
                400);
        }

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // Global lock order for settings mutation: User -> WishlistItem.
        var customer = await db.UserAccounts
            .FromSqlInterpolated($"SELECT * FROM store.user_accounts WHERE id = {customerId} FOR NO KEY UPDATE")
            .SingleOrDefaultAsync(ct)
            ?? throw new WishlistFailure("UNAUTHORIZED", "Tài khoản khách hàng không tồn tại.", 401);

        var item = await db.CustomerWishlistItems
            .FromSqlInterpolated($"""
                SELECT * FROM store.customer_wishlist_items
                WHERE customer_id = {customerId} AND product_id = {productId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(ct)
            ?? throw NotFound();

        var state = await ReadProductStateAsync(productId, ct);
        var samePayload = item.BackInStockEnabled == request.BackInStockEnabled
            && item.PriceDropEnabled == request.PriceDropEnabled
            && item.EmailEnabled == request.EmailEnabled;

        // A replay after a successful response was lost is safe even when it carries
        // the previous revision: return the committed state without another write.
        if (item.AlertRevision != request.ExpectedRevision)
        {
            if (samePayload)
            {
                await tx.CommitAsync(ct);
                return ToDto(item, state);
            }

            throw new WishlistFailure(
                "STALE_WISHLIST_ALERT_SETTINGS",
                "Cấu hình cảnh báo đã thay đổi. Vui lòng tải lại trước khi lưu.",
                409,
                item.AlertRevision);
        }

        if (samePayload)
        {
            await tx.CommitAsync(ct);
            return ToDto(item, state);
        }

        if (request.EmailEnabled && customer.EmailVerifiedAt is null)
        {
            throw new WishlistFailure(
                "EMAIL_CHANNEL_REQUIRES_VERIFIED_EMAIL",
                "Hãy xác minh địa chỉ email trước khi bật cảnh báo qua email.",
                400);
        }

        var enablesNewAlert = request.BackInStockEnabled && !item.BackInStockEnabled
            || request.PriceDropEnabled && !item.PriceDropEnabled;
        var enablesEmail = request.EmailEnabled && !item.EmailEnabled;
        if (!state.IsPublic && (enablesNewAlert || enablesEmail))
        {
            throw new WishlistFailure(
                "WISHLIST_ALERT_PRODUCT_UNAVAILABLE",
                "Không thể bật cảnh báo mới cho sản phẩm không còn mở bán.",
                409);
        }

        if (item.AlertRevision == int.MaxValue)
        {
            throw new WishlistFailure(
                "WISHLIST_ALERT_REVISION_EXHAUSTED",
                "Cấu hình cảnh báo không thể cập nhật thêm.",
                409);
        }

        if (!request.BackInStockEnabled)
        {
            item.LastObservedInStock = null;
        }
        else if (!item.BackInStockEnabled || item.LastObservedInStock is null)
        {
            item.LastObservedInStock = state.InStock;
        }

        if (!request.PriceDropEnabled)
        {
            item.LastObservedMinPrice = null;
        }
        else if (!item.PriceDropEnabled || item.LastObservedMinPrice is null)
        {
            item.LastObservedMinPrice = state.MinPrice;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        item.BackInStockEnabled = request.BackInStockEnabled;
        item.PriceDropEnabled = request.PriceDropEnabled;
        item.EmailEnabled = request.EmailEnabled;
        item.AlertRevision++;
        item.LastEvaluatedAt = now;
        item.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(item, state);
    }

    private async Task<ProductAlertState> ReadProductStateAsync(Guid productId, CancellationToken ct)
    {
        var raw = await db.Products
            .AsNoTracking()
            .Where(p => p.Id == productId)
            .Select(p => new
            {
                IsPublic = p.PublicationStatus == "PUBLISHED"
                    && p.ProductSkus.Any(s => s.SkuStatus == "ACTIVE"),
                MinPrice = p.ProductSkus
                    .Where(s => s.SkuStatus == "ACTIVE")
                    .Select(s => (decimal?)s.RegularPrice)
                    .Min(),
                InStock = p.ProductSkus.Any(s =>
                    s.SkuStatus == "ACTIVE"
                    && s.InventoryBalance != null
                    && s.InventoryBalance.AvailableQty > 0)
            })
            .SingleOrDefaultAsync(ct);

        if (raw is null || !raw.IsPublic)
            return new ProductAlertState(false, false, null);
        return new ProductAlertState(true, raw.InStock, raw.MinPrice);
    }

    private static WishlistAlertSettingsDto ToDto(
        CustomerWishlistItem item,
        ProductAlertState state) => new(
            item.ProductId,
            item.BackInStockEnabled,
            item.PriceDropEnabled,
            item.EmailEnabled,
            state.IsPublic
                ? state.InStock ? WishlistAvailability.Available : WishlistAvailability.OutOfStock
                : WishlistAvailability.ProductUnavailable,
            state.IsPublic ? state.MinPrice : null,
            item.AlertRevision,
            item.UpdatedAt);

    private static WishlistFailure NotFound() => new(
        "WISHLIST_ITEM_NOT_FOUND",
        "Không tìm thấy sản phẩm trong danh sách yêu thích.",
        404);

    private sealed record ProductAlertState(bool IsPublic, bool InStock, decimal? MinPrice);
}
