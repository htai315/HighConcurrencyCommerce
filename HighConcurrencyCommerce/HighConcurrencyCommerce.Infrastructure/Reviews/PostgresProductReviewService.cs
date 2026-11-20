using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HighConcurrencyCommerce.Application.Reviews;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HighConcurrencyCommerce.Infrastructure.Reviews;

public sealed class PostgresProductReviewService(
    StoreDbContext db,
    TimeProvider timeProvider) : IProductReviewService
{
    public static string MaskDisplayName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            return "Khách hàng";

        var trimmed = fullName.Trim();
        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            var word = parts[0];
            if (word.Length <= 2) return $"{word[0]}*";
            return $"{word[0]}***{word[^1]}";
        }

        var first = parts[0];
        var last = parts[^1];
        var maskedFirst = first.Length <= 2 ? $"{first[0]}*" : $"{first[0]}***{first[^1]}";
        var maskedLast = last.Length <= 2 ? $"{last[0]}*" : $"{last[0]}***{last[^1]}";
        return $"{maskedFirst} {maskedLast}";
    }

    public async Task<ReviewEligibilityDto> GetEligibilityAsync(
        Guid customerId,
        Guid productId,
        CancellationToken cancellationToken = default)
    {
        var existingReview = await db.ProductReviews
            .AsNoTracking()
            .Include(r => r.Product)
            .Where(r => r.CustomerId == customerId && r.ProductId == productId && r.DeletedAt == null)
            .Select(r => new MyProductReviewDto(
                r.Id,
                r.ProductId,
                r.Product.Title,
                r.Rating,
                r.Content,
                r.ReviewStatus,
                r.CreatedAt,
                r.UpdatedAt,
                r.ModeratedAt,
                r.ModerationReason))
            .FirstOrDefaultAsync(cancellationToken);

        if (existingReview != null)
        {
            return new ReviewEligibilityDto(false, "ALREADY_REVIEWED", existingReview);
        }

        var qualifyingItems = await (
            from item in db.SalesOrderItems
            join sku in db.ProductSkus on item.ProductSkuId equals sku.Id
            join order in db.SalesOrders on item.OrderId equals order.Id
            join shipment in db.Shipments on order.Id equals shipment.OrderId
            where sku.ProductId == productId
               && order.CustomerId == customerId
               && order.OrderStatus == "COMPLETED"
               && shipment.ShipmentStatus == "DELIVERED"
               && shipment.DeliveredAt != null
            select new { item.Id, item.Quantity }
        ).ToListAsync(cancellationToken);

        if (qualifyingItems.Count == 0)
        {
            return new ReviewEligibilityDto(false, "NO_DELIVERED_ORDER", null);
        }

        var qualifyingItemIds = qualifyingItems.Select(x => x.Id).ToList();
        var totalPurchased = qualifyingItems.Sum(x => x.Quantity);

        var totalRefunded = await (
            from rl in db.OrderReturnRequestLines
            join r in db.OrderReturnRequests on rl.ReturnRequestId equals r.Id
            where qualifyingItemIds.Contains(rl.OrderItemId) && r.ReturnStatus == "REFUNDED"
            select rl.RequestedQty
        ).SumAsync(cancellationToken);

        if (totalPurchased <= totalRefunded)
        {
            return new ReviewEligibilityDto(false, "NO_DELIVERED_ORDER", null);
        }

        return new ReviewEligibilityDto(true, null, null);
    }

    public async Task<MyProductReviewDto> CreateReviewAsync(
        Guid customerId,
        Guid productId,
        CreateProductReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRatingAndContent(request.Rating, request.Content);

        var trimmedContent = request.Content.Trim();

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // Serialize review creation for the customer without conflicting with the
        // KEY SHARE lock taken by refund-side inserts that reference user_accounts.
        // Both review and refund lock Order rows afterwards, so FOR UPDATE here
        // would create a User -> Order / Order -> User deadlock cycle.
        var customer = await db.UserAccounts
            .FromSqlInterpolated($"SELECT * FROM store.user_accounts WHERE id = {customerId} FOR NO KEY UPDATE")
            .FirstOrDefaultAsync(cancellationToken);

        if (customer == null)
        {
            throw new ProductReviewFailure("VALIDATION_ERROR", "Tài khoản khách hàng không tồn tại.", 400);
        }

        // Check Product existence and publication status
        var product = await db.Products
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == productId, cancellationToken);

        if (product == null)
        {
            throw new ProductReviewFailure("PRODUCT_NOT_FOUND", "Sản phẩm không tồn tại.", 404);
        }

        if (product.PublicationStatus != "PUBLISHED")
        {
            throw new ProductReviewFailure("PRODUCT_NOT_FOUND", "Sản phẩm chưa được phát hành.", 404);
        }

        // Check if active review already exists
        var existingActive = await db.ProductReviews
            .AnyAsync(r => r.CustomerId == customerId && r.ProductId == productId && r.DeletedAt == null, cancellationToken);

        if (existingActive)
        {
            throw new ProductReviewFailure("REVIEW_ALREADY_EXISTS", "Bạn đã có đánh giá đang hoạt động cho sản phẩm này.", 409);
        }

        // Find qualifying order items
        var qualifyingItems = await (
            from item in db.SalesOrderItems
            join sku in db.ProductSkus on item.ProductSkuId equals sku.Id
            join order in db.SalesOrders on item.OrderId equals order.Id
            join shipment in db.Shipments on order.Id equals shipment.OrderId
            where sku.ProductId == productId
               && order.CustomerId == customerId
               && order.OrderStatus == "COMPLETED"
               && shipment.ShipmentStatus == "DELIVERED"
               && shipment.DeliveredAt != null
            orderby order.CreatedAt descending
            select new { item.Id, item.Quantity, item.ProductTitleSnapshot, OrderId = order.Id }
        ).ToListAsync(cancellationToken);

        if (qualifyingItems.Count == 0)
        {
            throw new ProductReviewFailure("REVIEW_NOT_ELIGIBLE", "Chỉ khách hàng đã nhận hàng thành công mới được đánh giá sản phẩm.", 409);
        }

        var qualifyingOrderIds = qualifyingItems.Select(x => x.OrderId).Distinct().OrderBy(id => id).ToArray();
        if (qualifyingOrderIds.Length > 0)
        {
            await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM store.sales_orders WHERE id = ANY({qualifyingOrderIds}) ORDER BY id FOR UPDATE")
                .ToListAsync(cancellationToken);
        }

        var qualifyingItemIds = qualifyingItems.Select(x => x.Id).ToList();
        var totalPurchased = qualifyingItems.Sum(x => x.Quantity);

        var totalRefunded = await (
            from rl in db.OrderReturnRequestLines
            join r in db.OrderReturnRequests on rl.ReturnRequestId equals r.Id
            where qualifyingItemIds.Contains(rl.OrderItemId) && r.ReturnStatus == "REFUNDED"
            select rl.RequestedQty
        ).SumAsync(cancellationToken);

        if (totalPurchased <= totalRefunded)
        {
            throw new ProductReviewFailure("REVIEW_NOT_ELIGIBLE", "Chỉ khách hàng đã nhận hàng thành công mới được đánh giá sản phẩm.", 409);
        }

        var qualifyingItem = qualifyingItems[0];

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var review = new ProductReview
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now,
            CustomerId = customerId,
            ProductId = productId,
            QualifyingOrderItemId = qualifyingItem.Id,
            Rating = request.Rating,
            Content = trimmedContent,
            ReviewStatus = "PUBLISHED",
            ModeratedBy = null,
            ModeratedAt = null,
            ModerationReason = null,
            DeletedAt = null
        };

        db.ProductReviews.Add(review);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            throw new ProductReviewFailure("REVIEW_ALREADY_EXISTS", "Bạn đã có đánh giá đang hoạt động cho sản phẩm này.", 409);
        }

        return new MyProductReviewDto(
            review.Id,
            review.ProductId,
            product.Title,
            review.Rating,
            review.Content,
            review.ReviewStatus,
            review.CreatedAt,
            review.UpdatedAt,
            review.ModeratedAt,
            review.ModerationReason);
    }

    public async Task<IReadOnlyList<MyProductReviewDto>> GetMyReviewsAsync(
        Guid customerId,
        CancellationToken cancellationToken = default)
    {
        return await db.ProductReviews
            .AsNoTracking()
            .Include(r => r.Product)
            .Where(r => r.CustomerId == customerId && r.DeletedAt == null)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new MyProductReviewDto(
                r.Id,
                r.ProductId,
                r.Product.Title,
                r.Rating,
                r.Content,
                r.ReviewStatus,
                r.CreatedAt,
                r.UpdatedAt,
                r.ModeratedAt,
                r.ModerationReason))
            .ToListAsync(cancellationToken);
    }

    public async Task<MyProductReviewDto> UpdateReviewAsync(
        Guid customerId,
        Guid reviewId,
        UpdateProductReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRatingAndContent(request.Rating, request.Content);

        var trimmedContent = request.Content.Trim();

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var review = await db.ProductReviews
            .FromSqlInterpolated($"SELECT * FROM store.product_reviews WHERE id = {reviewId} FOR UPDATE")
            .Include(r => r.Product)
            .FirstOrDefaultAsync(cancellationToken);

        if (review == null || review.DeletedAt != null || review.CustomerId != customerId)
        {
            throw new ProductReviewFailure("REVIEW_NOT_FOUND", "Không tìm thấy đánh giá.", 404);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        review.Rating = request.Rating;
        review.Content = trimmedContent;
        review.UpdatedAt = now;

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return new MyProductReviewDto(
            review.Id,
            review.ProductId,
            review.Product.Title,
            review.Rating,
            review.Content,
            review.ReviewStatus,
            review.CreatedAt,
            review.UpdatedAt,
            review.ModeratedAt,
            review.ModerationReason);
    }

    public async Task DeleteReviewAsync(
        Guid customerId,
        Guid reviewId,
        CancellationToken cancellationToken = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var review = await db.ProductReviews
            .FromSqlInterpolated($"SELECT * FROM store.product_reviews WHERE id = {reviewId} FOR UPDATE")
            .FirstOrDefaultAsync(cancellationToken);

        if (review == null || review.DeletedAt != null || review.CustomerId != customerId)
        {
            throw new ProductReviewFailure("REVIEW_NOT_FOUND", "Không tìm thấy đánh giá.", 404);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        review.DeletedAt = now;
        review.UpdatedAt = now;

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<ProductRatingSummaryDto> GetProductRatingSummaryAsync(
        Guid productId,
        CancellationToken cancellationToken = default)
    {
        await EnsurePublishedProductAsync(productId, cancellationToken);
        var publicReviewsQuery = db.ProductReviews
            .AsNoTracking()
            .Where(r => r.ProductId == productId && r.Product.PublicationStatus == "PUBLISHED" && r.ReviewStatus == "PUBLISHED" && r.DeletedAt == null);

        var ratingGroups = await publicReviewsQuery
            .GroupBy(r => (int)r.Rating)
            .Select(g => new { Rating = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        int one = 0, two = 0, three = 0, four = 0, five = 0;
        int totalCount = 0;
        long totalSum = 0;

        foreach (var g in ratingGroups)
        {
            totalCount += g.Count;
            totalSum += (long)g.Rating * g.Count;
            switch (g.Rating)
            {
                case 1: one = g.Count; break;
                case 2: two = g.Count; break;
                case 3: three = g.Count; break;
                case 4: four = g.Count; break;
                case 5: five = g.Count; break;
            }
        }

        decimal average = totalCount == 0 ? 0.00m : Math.Round((decimal)totalSum / totalCount, 2, MidpointRounding.AwayFromZero);
        return new ProductRatingSummaryDto(average, totalCount, new RatingDistributionDto(one, two, three, four, five));
    }

    public async Task<ProductReviewPageDto> GetPublicReviewsAsync(
        Guid productId,
        ReviewQuery query,
        CancellationToken cancellationToken = default)
    {
        if (query.Rating is < 1 or > 5)
            throw new ProductReviewFailure("VALIDATION_ERROR", "Điểm đánh giá phải từ 1 đến 5 sao.", 400);

        // Product visibility, aggregate, filtered count and page share one database snapshot.
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var publicReviewsQuery = db.ProductReviews
            .AsNoTracking()
            .Include(r => r.Customer)
            .Where(r => r.ProductId == productId && r.ReviewStatus == "PUBLISHED" && r.DeletedAt == null);

        var summary = await GetProductRatingSummaryAsync(productId, cancellationToken);
        if (query.Rating.HasValue)
            publicReviewsQuery = publicReviewsQuery.Where(r => r.Rating == query.Rating.Value);
        var totalCount = query.Rating.HasValue
            ? await publicReviewsQuery.CountAsync(cancellationToken)
            : summary.ReviewCount;

        var clampedPage = Math.Clamp(query.Page ?? 1, 1, 100_000);
        var clampedPageSize = Math.Clamp(query.PageSize ?? 10, 1, 100);
        var skip = (clampedPage - 1) * clampedPageSize;

        IQueryable<ProductReview> sortedQuery = query.Sort?.ToLowerInvariant() switch
        {
            "highest" => publicReviewsQuery.OrderByDescending(r => r.Rating).ThenByDescending(r => r.CreatedAt).ThenBy(r => r.Id),
            "lowest" => publicReviewsQuery.OrderBy(r => r.Rating).ThenByDescending(r => r.CreatedAt).ThenBy(r => r.Id),
            _ => publicReviewsQuery.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id)
        };

        var items = await sortedQuery
            .Skip(skip)
            .Take(clampedPageSize)
            .Select(r => new PublicProductReviewDto(
                r.Id,
                r.Rating,
                r.Content,
                r.CreatedAt,
                r.UpdatedAt,
                MaskDisplayName(r.Customer.FullName),
                true))
            .ToListAsync(cancellationToken);

        await tx.CommitAsync(cancellationToken);
        return new ProductReviewPageDto(items, summary, clampedPage, clampedPageSize, totalCount);
    }

    private async Task EnsurePublishedProductAsync(Guid productId, CancellationToken cancellationToken)
    {
        if (!await db.Products.AsNoTracking().AnyAsync(p => p.Id == productId && p.PublicationStatus == "PUBLISHED", cancellationToken))
            throw new ProductReviewFailure("PRODUCT_NOT_FOUND", "Không tìm thấy sản phẩm đã công bố.", 404);
    }

    public async Task<ModerationProductReviewPageDto> GetModerationReviewsAsync(
        ReviewModerationQuery query,
        CancellationToken cancellationToken = default)
    {
        var reviewsQuery = db.ProductReviews
            .AsNoTracking()
            .Include(r => r.Product)
            .Include(r => r.Customer)
            .Include(r => r.ModeratedByNavigation)
            .Where(r => r.DeletedAt == null);

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var normalizedStatus = query.Status.Trim().ToUpperInvariant();
            reviewsQuery = reviewsQuery.Where(r => r.ReviewStatus == normalizedStatus);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            reviewsQuery = reviewsQuery.Where(r =>
                r.Product.Title.Contains(search) ||
                r.Customer.FullName.Contains(search) ||
                r.Customer.Email.Contains(search) ||
                r.Content.Contains(search));
        }

        var totalCount = await reviewsQuery.CountAsync(cancellationToken);

        var clampedPage = Math.Clamp(query.Page ?? 1, 1, 100_000);
        var clampedPageSize = Math.Clamp(query.PageSize ?? 20, 1, 100);
        var skip = (clampedPage - 1) * clampedPageSize;

        var items = await reviewsQuery
            .OrderByDescending(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .Skip(skip)
            .Take(clampedPageSize)
            .Select(r => new ModerationProductReviewDto(
                r.Id,
                r.ProductId,
                r.Product.Title,
                r.CustomerId,
                r.Customer.FullName,
                r.Customer.Email,
                r.Rating,
                r.Content,
                r.ReviewStatus,
                r.CreatedAt,
                r.UpdatedAt,
                r.ModeratedBy,
                r.ModeratedByNavigation != null ? r.ModeratedByNavigation.FullName : null,
                r.ModeratedAt,
                r.ModerationReason,
                r.DeletedAt))
            .ToListAsync(cancellationToken);

        return new ModerationProductReviewPageDto(items, clampedPage, clampedPageSize, totalCount);
    }

    public async Task<ModerationProductReviewDto> ModerateReviewAsync(
        Guid moderatorId,
        string moderatorRole,
        Guid reviewId,
        ModerateProductReviewRequest request,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var action = request.Action?.Trim().ToUpperInvariant();
        if (action is not ("HIDE" or "RESTORE"))
        {
            throw new ProductReviewFailure("VALIDATION_ERROR", "Hành động chỉ có thể là HIDE hoặc RESTORE.", 400);
        }

        if (action == "HIDE")
        {
            if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < 3 || request.Reason.Trim().Length > 500)
            {
                throw new ProductReviewFailure("VALIDATION_ERROR", "Lý do ẩn đánh giá từ 3 đến 500 ký tự.", 400);
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.Reason) && request.Reason.Trim().Length > 500)
        {
            throw new ProductReviewFailure("VALIDATION_ERROR", "Lý do khôi phục tối đa 500 ký tự.", 400);
        }

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var review = await db.ProductReviews
            .FromSqlInterpolated($"SELECT * FROM store.product_reviews WHERE id = {reviewId} FOR UPDATE")
            .Include(r => r.Product)
            .Include(r => r.Customer)
            .Include(r => r.ModeratedByNavigation)
            .FirstOrDefaultAsync(cancellationToken);

        if (review == null || review.DeletedAt != null)
        {
            throw new ProductReviewFailure("REVIEW_NOT_FOUND", "Không tìm thấy đánh giá.", 404);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        if (action == "HIDE")
        {
            if (review.ReviewStatus == "HIDDEN")
            {
                throw new ProductReviewFailure("INVALID_REVIEW_STATE", "Đánh giá đã ở trạng thái ẩn.", 409);
            }

            var before = new { status = review.ReviewStatus, reason = review.ModerationReason, moderatedBy = review.ModeratedBy };

            review.ReviewStatus = "HIDDEN";
            review.ModeratedBy = moderatorId;
            review.ModeratedAt = now;
            review.ModerationReason = request.Reason!.Trim();
            review.UpdatedAt = now;

            var after = new { status = review.ReviewStatus, reason = review.ModerationReason, moderatedBy = review.ModeratedBy };

            db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                ActorId = moderatorId,
                ActorType = "USER",
                Action = "PRODUCT_REVIEW_HIDDEN",
                EntityType = "ProductReview",
                EntityId = reviewId,
                BeforeData = JsonSerializer.Serialize(before),
                AfterData = JsonSerializer.Serialize(after),
                CorrelationId = correlationId,
                CreatedAt = now,
                UpdatedAt = now,
                OccurredAt = now
            });
        }
        else // RESTORE
        {
            if (review.ReviewStatus == "PUBLISHED")
            {
                throw new ProductReviewFailure("INVALID_REVIEW_STATE", "Đánh giá đang ở trạng thái công khai.", 409);
            }

            var before = new { status = review.ReviewStatus, reason = review.ModerationReason, moderatedBy = review.ModeratedBy };

            review.ReviewStatus = "PUBLISHED";
            review.ModeratedBy = null;
            review.ModeratedAt = null;
            review.ModerationReason = null;
            review.UpdatedAt = now;

            var after = new { status = review.ReviewStatus, reason = (string?)null, moderatedBy = (Guid?)null };

            db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                ActorId = moderatorId,
                ActorType = "USER",
                Action = "PRODUCT_REVIEW_RESTORED",
                EntityType = "ProductReview",
                EntityId = reviewId,
                BeforeData = JsonSerializer.Serialize(before),
                AfterData = JsonSerializer.Serialize(after),
                CorrelationId = correlationId,
                CreatedAt = now,
                UpdatedAt = now,
                OccurredAt = now
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        // Fetch moderator name if needed
        string? moderatorName = null;
        if (review.ModeratedBy.HasValue)
        {
            moderatorName = await db.UserAccounts
                .Where(u => u.Id == review.ModeratedBy.Value)
                .Select(u => u.FullName)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return new ModerationProductReviewDto(
            review.Id,
            review.ProductId,
            review.Product.Title,
            review.CustomerId,
            review.Customer.FullName,
            review.Customer.Email,
            review.Rating,
            review.Content,
            review.ReviewStatus,
            review.CreatedAt,
            review.UpdatedAt,
            review.ModeratedBy,
            moderatorName,
            review.ModeratedAt,
            review.ModerationReason,
            review.DeletedAt);
    }

    private static void ValidateRatingAndContent(short rating, string? content)
    {
        if (rating is < 1 or > 5)
        {
            throw new ProductReviewFailure("VALIDATION_ERROR", "Điểm đánh giá phải từ 1 đến 5 sao.", 400);
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ProductReviewFailure("VALIDATION_ERROR", "Nội dung nhận xét không được để trống.", 400);
        }

        var trimmed = content.Trim();
        if (trimmed.Length is < 10 or > 2000)
        {
            throw new ProductReviewFailure("VALIDATION_ERROR", "Nội dung nhận xét từ 10 đến 2.000 ký tự.", 400);
        }
    }
}

