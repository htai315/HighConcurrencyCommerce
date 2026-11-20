using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HighConcurrencyCommerce.Application.Catalog;
using HighConcurrencyCommerce.Application.Wishlist;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HighConcurrencyCommerce.Infrastructure.Wishlist;

public sealed class PostgresWishlistService : IWishlistService
{
    private readonly StoreDbContext _dbContext;

    public PostgresWishlistService(StoreDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task AddItemAsync(Guid customerId, Guid productId, CancellationToken cancellationToken = default)
    {
        await using var tx = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // 1. Lock customer row FOR NO KEY UPDATE to serialize mutations per customer
        var customer = await _dbContext.UserAccounts
            .FromSqlInterpolated($"SELECT * FROM store.user_accounts WHERE id = {customerId} FOR NO KEY UPDATE")
            .FirstOrDefaultAsync(cancellationToken);

        if (customer == null)
        {
            throw new WishlistFailure("UNAUTHORIZED", "Tài khoản khách hàng không tồn tại.", 401);
        }

        // 2. Check if already saved in wishlist -> Idempotent no-op (204) even if full or unlisted
        var alreadySaved = await _dbContext.CustomerWishlistItems
            .AnyAsync(w => w.CustomerId == customerId && w.ProductId == productId, cancellationToken);

        if (alreadySaved)
        {
            await tx.CommitAsync(cancellationToken);
            return;
        }

        var lockedProductId = await _dbContext.Database.SqlQuery<Guid>(
            $"SELECT id AS \"Value\" FROM store.products WHERE id = {productId} FOR SHARE")
            .FirstOrDefaultAsync(cancellationToken);
        if (lockedProductId == Guid.Empty)
            throw new WishlistFailure("PRODUCT_NOT_FOUND", "Sản phẩm không tồn tại hoặc chưa được công bố bán.", 404);

        // 3. Verify product is public and has at least one active SKU
        var isPublic = await _dbContext.Products
            .AsNoTracking()
            .Where(p => p.Id == productId && p.PublicationStatus == "PUBLISHED" && p.ProductSkus.Any(s => s.SkuStatus == "ACTIVE"))
            .AnyAsync(cancellationToken);

        if (!isPublic)
        {
            throw new WishlistFailure("PRODUCT_NOT_FOUND", "Sản phẩm không tồn tại hoặc chưa được công bố bán.", 404);
        }

        // 4. Check limit (max 500 items)
        var count = await _dbContext.CustomerWishlistItems
            .Where(w => w.CustomerId == customerId)
            .CountAsync(cancellationToken);

        if (count >= 500)
        {
            throw new WishlistFailure("WISHLIST_LIMIT_REACHED", "Danh sách yêu thích đã đạt giới hạn tối đa 500 sản phẩm.", 409);
        }

        // 5. Insert with ON CONFLICT DO NOTHING
        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO store.customer_wishlist_items (customer_id, product_id, created_at) VALUES ({customerId}, {productId}, now()) ON CONFLICT (customer_id, product_id) DO NOTHING",
            cancellationToken);

        await tx.CommitAsync(cancellationToken);
    }

    public async Task RemoveItemAsync(Guid customerId, Guid productId, CancellationToken cancellationToken = default)
    {
        await using var tx = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // Lock customer row FOR NO KEY UPDATE
        var customer = await _dbContext.UserAccounts
            .FromSqlInterpolated($"SELECT * FROM store.user_accounts WHERE id = {customerId} FOR NO KEY UPDATE")
            .FirstOrDefaultAsync(cancellationToken);

        if (customer == null)
        {
            throw new WishlistFailure("UNAUTHORIZED", "Tài khoản khách hàng không tồn tại.", 401);
        }

        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM store.customer_wishlist_items WHERE customer_id = {customerId} AND product_id = {productId}",
            cancellationToken);

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<WishlistSummaryDto> GetSummaryAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        var savedItems = await _dbContext.CustomerWishlistItems
            .AsNoTracking()
            .Where(w => w.CustomerId == customerId)
            .OrderByDescending(w => w.CreatedAt)
            .ThenBy(w => w.ProductId)
            .Take(500)
            .Select(w => new WishlistAlertSummaryDto(
                w.ProductId,
                w.BackInStockEnabled,
                w.PriceDropEnabled,
                w.EmailEnabled,
                w.AlertRevision))
            .ToListAsync(cancellationToken);

        return new WishlistSummaryDto(
            savedItems.Select(x => x.ProductId).ToList(),
            savedItems.Count,
            savedItems);
    }

    public async Task<WishlistPageDto> GetWishlistAsync(Guid customerId, int page = 1, int pageSize = 12, CancellationToken cancellationToken = default)
    {
        if (page < 1 || page > 100000)
        {
            throw new WishlistFailure("VALIDATION_ERROR", "Trang không hợp lệ. Phải từ 1 đến 100.000.", 400);
        }
        if (pageSize < 1 || pageSize > 50)
        {
            throw new WishlistFailure("VALIDATION_ERROR", "Số mục mỗi trang không hợp lệ. Phải từ 1 đến 50.", 400);
        }

        await using var tx = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);

        var baseQuery = _dbContext.CustomerWishlistItems
            .AsNoTracking()
            .Where(w => w.CustomerId == customerId);

        var totalCount = await baseQuery.CountAsync(cancellationToken);

        long offset = ((long)page - 1) * pageSize;
        if (offset > int.MaxValue || offset >= totalCount)
        {
            await tx.CommitAsync(cancellationToken);
            return new WishlistPageDto([], page, pageSize, totalCount);
        }

        var raw = await baseQuery
            .OrderByDescending(w => w.CreatedAt)
            .ThenBy(w => w.ProductId)
            .Skip((int)offset)
            .Take(pageSize)
            .Select(w => new
            {
                w.ProductId,
                w.CreatedAt,
                IsPublic = w.Product.PublicationStatus == "PUBLISHED" && w.Product.ProductSkus.Any(s => s.SkuStatus == "ACTIVE"),
                Title = w.Product.Title,
                CategoryId = w.Product.CategoryId,
                CategoryName = w.Product.Category != null && w.Product.Category.IsVisible ? w.Product.Category.Name : null,
                CategorySlug = w.Product.Category != null && w.Product.Category.IsVisible ? w.Product.Category.Slug : null,
                MinPrice = w.Product.ProductSkus.Where(s => s.SkuStatus == "ACTIVE").Select(s => (decimal?)s.RegularPrice).Min(),
                InStock = w.Product.ProductSkus.Any(s => s.SkuStatus == "ACTIVE" && s.InventoryBalance != null && s.InventoryBalance.AvailableQty > 0),
                ImageUrl = w.Product.ProductImages.Where(img => img.ImageStatus == "ACTIVE").OrderBy(img => img.DisplayOrder).ThenBy(img => img.CreatedAt).Select(img => img.FileKey).FirstOrDefault(),
                HasReviews = w.Product.ProductReviews.Any(r => r.ReviewStatus == "PUBLISHED" && r.DeletedAt == null),
                AvgRating = w.Product.ProductReviews.Where(r => r.ReviewStatus == "PUBLISHED" && r.DeletedAt == null).Select(r => (decimal?)r.Rating).Average(),
                ReviewCount = w.Product.ProductReviews.Count(r => r.ReviewStatus == "PUBLISHED" && r.DeletedAt == null),
                w.BackInStockEnabled,
                w.PriceDropEnabled,
                w.EmailEnabled,
                w.AlertRevision
            })
            .ToListAsync(cancellationToken);

        await tx.CommitAsync(cancellationToken);

        var items = raw.Select(r =>
        {
            if (!r.IsPublic)
            {
                return new WishlistItemDto(
                    r.ProductId,
                    r.CreatedAt,
                    WishlistAvailability.ProductUnavailable,
                    null,
                    new WishlistAlertSummaryDto(
                        r.ProductId,
                        r.BackInStockEnabled,
                        r.PriceDropEnabled,
                        r.EmailEnabled,
                        r.AlertRevision));
            }

            var category = r.CategoryId.HasValue && r.CategoryName != null && r.CategorySlug != null
                ? new CategoryDto(r.CategoryId.Value, r.CategoryName, r.CategorySlug)
                : null;

            var productSummary = new ProductSummaryDto(
                r.ProductId,
                r.Title,
                category,
                null,
                r.MinPrice ?? 0m,
                r.InStock,
                r.ImageUrl,
                r.HasReviews && r.AvgRating.HasValue ? Math.Round(r.AvgRating.Value, 2) : 0.00m,
                r.ReviewCount);

            var availability = r.InStock ? WishlistAvailability.Available : WishlistAvailability.OutOfStock;

            return new WishlistItemDto(
                r.ProductId,
                r.CreatedAt,
                availability,
                productSummary,
                new WishlistAlertSummaryDto(
                    r.ProductId,
                    r.BackInStockEnabled,
                    r.PriceDropEnabled,
                    r.EmailEnabled,
                    r.AlertRevision));
        }).ToList();

        return new WishlistPageDto(items, page, pageSize, totalCount);
    }
}

