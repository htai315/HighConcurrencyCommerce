using System.Data;
using System.Text.Json;
using HighConcurrencyCommerce.Application.ProductLifecycle;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace HighConcurrencyCommerce.Infrastructure.ProductLifecycle;

public sealed class PostgresProductLifecycleService(StoreDbContext db, TimeProvider timeProvider) : IProductLifecycleService
{
    public async Task<ProductLifecycleDto> SuspendAsync(Guid adminId, Guid productId, SuspendProductRequest request, string correlationId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var product = await LockProduct(productId, ct);
        EnsureState(product, "PUBLISHED", "Chỉ sản phẩm đang mở bán mới có thể tạm ngừng.");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var reason = request.Reason.Trim();
        Transition(product, adminId, "SUSPENDED", reason, now);
        AddAudit(adminId, productId, "PRODUCT_SUSPENDED", "PUBLISHED", "SUSPENDED", reason, correlationId, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(product);
    }

    public async Task<ProductLifecycleDto> ResumeAsync(Guid adminId, Guid productId, string correlationId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var product = await LockProduct(productId, ct);
        EnsureState(product, "SUSPENDED", "Chỉ sản phẩm đang tạm ngừng mới có thể mở bán lại.");

        await db.Entry(product).Collection(x => x.ProductSkus).LoadAsync(ct);
        var activeSkus = product.ProductSkus.Where(x => x.SkuStatus == "ACTIVE").ToList();
        if (activeSkus.Count == 0 || activeSkus.Any(x => x.RegularPrice <= 0))
            throw Fail("PRODUCT_RESUME_REQUIREMENTS_NOT_MET", "Sản phẩm phải có ít nhất một SKU ACTIVE và mọi SKU ACTIVE phải có giá lớn hơn 0.");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var previousReason = product.StatusReason;
        Transition(product, adminId, "PUBLISHED", null, now);
        AddAudit(adminId, productId, "PRODUCT_RESUMED", "SUSPENDED", "PUBLISHED", previousReason, correlationId, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(product);
    }

    public async Task<ProductLifecycleDto> ArchiveAsync(Guid adminId, Guid productId, ArchiveProductRequest request, string correlationId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var product = await LockProduct(productId, ct);
        EnsureState(product, "SUSPENDED", "Chỉ sản phẩm đang tạm ngừng mới có thể lưu trữ.");

        var hasOpenRevision = await db.ProductRevisions.AnyAsync(x => x.ProductId == productId
            && (x.RevisionStatus == "DRAFT" || x.RevisionStatus == "PENDING_REVIEW"), ct);
        if (hasOpenRevision)
            throw Fail("PRODUCT_ARCHIVE_OPEN_REVISION", "Hãy hủy hoặc xử lý bản chỉnh sửa đang mở trước khi lưu trữ sản phẩm.");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var hasEffectiveOffer = await db.FlashSaleOffers.AnyAsync(x => x.ProductSku.ProductId == productId
            && (x.Campaign.CampaignStatus == "ACTIVE" || x.Campaign.CampaignStatus == "SCHEDULED")
            && x.Campaign.EndsAt > now, ct);
        if (hasEffectiveOffer)
            throw Fail("PRODUCT_ARCHIVE_ACTIVE_FLASH_SALE", "Sản phẩm còn thuộc Flash Sale đang hoặc sắp có hiệu lực.");

        var reason = request.Reason.Trim();
        Transition(product, adminId, "ARCHIVED", reason, now);
        AddAudit(adminId, productId, "PRODUCT_ARCHIVED", "SUSPENDED", "ARCHIVED", reason, correlationId, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(product);
    }

    private async Task<Product> LockProduct(Guid productId, CancellationToken ct) =>
        await db.Products.FromSqlInterpolated($"SELECT * FROM store.products WHERE id = {productId} FOR UPDATE")
            .SingleOrDefaultAsync(ct)
        ?? throw new ProductLifecycleFailure("PRODUCT_NOT_FOUND", "Không tìm thấy sản phẩm.", 404);

    private static void EnsureState(Product product, string expected, string message)
    {
        if (product.PublicationStatus != expected)
            throw Fail("PRODUCT_LIFECYCLE_INVALID_TRANSITION", message);
    }

    private static void Transition(Product product, Guid actorId, string status, string? reason, DateTime now)
    {
        product.PublicationStatus = status;
        product.StatusChangedAt = now;
        product.StatusChangedBy = actorId;
        product.StatusReason = reason;
        product.UpdatedAt = now;
    }

    private void AddAudit(Guid actorId, Guid productId, string action, string before, string after, string? reason, string correlationId, DateTime now) =>
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(), ActorId = actorId, ActorType = "USER", Action = action,
            EntityType = "Product", EntityId = productId,
            BeforeData = JsonSerializer.Serialize(new { publicationStatus = before }),
            AfterData = JsonSerializer.Serialize(new { publicationStatus = after, reason }),
            CorrelationId = correlationId, OccurredAt = now, CreatedAt = now, UpdatedAt = now
        });

    private static ProductLifecycleDto ToDto(Product product) =>
        new(product.Id, product.PublicationStatus, product.StatusChangedAt, product.StatusChangedBy, product.StatusReason);

    private static ProductLifecycleFailure Fail(string code, string message) => new(code, message);
}
