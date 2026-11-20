using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using HighConcurrencyCommerce.Application.ProductManagement;
using HighConcurrencyCommerce.Application.ProductRevisions;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HighConcurrencyCommerce.Infrastructure.ProductRevisions;

public sealed class PostgresProductRevisionService(StoreDbContext db, IProductMediaStorage storage, IProductRevisionApprovalCheckpoint approvalCheckpoint, IServiceScopeFactory scopeFactory, ILogger<PostgresProductRevisionService> logger) : IProductRevisionService
{
    public async Task<ProductRevisionDto?> GetOpenAsync(Guid productId, CancellationToken ct = default)
    {
        var revision = await RevisionQuery().AsNoTracking().SingleOrDefaultAsync(x => x.ProductId == productId && (x.RevisionStatus == "DRAFT" || x.RevisionStatus == "PENDING_REVIEW"), ct);
        return revision is null ? null : ToDto(revision);
    }

    public async Task<ProductRevisionDto> GetAsync(Guid revisionId, CancellationToken ct = default) =>
        ToDto(await RevisionQuery().AsNoTracking().SingleOrDefaultAsync(x => x.Id == revisionId, ct)
            ?? throw Fail("REVISION_NOT_FOUND", "Không tìm thấy bản chỉnh sửa sản phẩm."));

    public async Task<IReadOnlyList<ProductRevisionSummaryDto>> GetQueueAsync(string? status, CancellationToken ct = default)
    {
        var normalized = string.IsNullOrWhiteSpace(status) ? "PENDING_REVIEW" : status.Trim().ToUpperInvariant();
        if (!Statuses.Contains(normalized)) throw Fail("VALIDATION_ERROR", "Trạng thái revision không hợp lệ.");
        return await db.ProductRevisions.AsNoTracking().Where(x => x.RevisionStatus == normalized)
            .OrderBy(x => x.SubmittedAt ?? x.CreatedAt).ThenBy(x => x.Id)
            .Select(x => new ProductRevisionSummaryDto(x.Id, x.ProductId, x.RevisionNo, x.RevisionStatus, x.Product.Title, x.BaseContentVersion, x.CreatedAt, x.SubmittedAt, x.ReviewNote))
            .ToListAsync(ct);
    }

    public async Task<ProductRevisionDto> CreateAsync(Guid staffId, Guid productId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var product = await LockProduct(productId, ct);
        if (product.PublicationStatus is not ("PUBLISHED" or "SUSPENDED")) throw Fail("REVISION_INVALID_STATE", "Chỉ sản phẩm đã công bố hoặc đang tạm ngừng mới tạo được bản chỉnh sửa.");
        var open = await db.ProductRevisions.AnyAsync(x => x.ProductId == productId && (x.RevisionStatus == "DRAFT" || x.RevisionStatus == "PENDING_REVIEW"), ct);
        if (open) throw Fail("REVISION_ALREADY_OPEN", "Sản phẩm đã có một bản chỉnh sửa đang mở.");
        var nextNo = (await db.ProductRevisions.Where(x => x.ProductId == productId).MaxAsync(x => (int?)x.RevisionNo, ct) ?? 0) + 1;
        var now = DateTime.UtcNow;
        var revision = new ProductRevision { Id = Guid.NewGuid(), ProductId = productId, RevisionNo = nextNo, RevisionStatus = "DRAFT", BaseContentVersion = product.ContentVersion, Title = product.Title, Description = product.Description, CategoryId = product.CategoryId, BrandId = product.BrandId, CreatedBy = staffId, CreatedAt = now, UpdatedAt = now };
        var images = await db.ProductImages.AsNoTracking().Where(x => x.ProductId == productId).OrderBy(x => x.DisplayOrder).ThenBy(x => x.Id).ToListAsync(ct);
        revision.Images = images.Select(x => new ProductRevisionImage { Id = Guid.NewGuid(), SourceImageId = x.Id, FileKey = x.FileKey, AltText = x.AltText, DisplayOrder = x.DisplayOrder, ImageStatus = x.ImageStatus, IsNewUpload = false, CreatedAt = now, UpdatedAt = now }).ToList();
        var specs = await db.ProductSpecifications.AsNoTracking().Where(x => x.ProductId == productId).OrderBy(x => x.DisplayOrder).ThenBy(x => x.Id).ToListAsync(ct);
        revision.Specifications = specs.Select(x => new ProductRevisionSpecification { Id = Guid.NewGuid(), SpecKey = x.SpecKey, DisplayName = x.DisplayName, SpecValue = x.SpecValue, DisplayOrder = x.DisplayOrder, CreatedAt = now, UpdatedAt = now }).ToList();
        db.ProductRevisions.Add(revision); db.AuditLogs.Add(Audit(staffId, "PRODUCT_REVISION_CREATED", revision.Id, null, new { productId, revisionNo = nextNo, baseContentVersion = product.ContentVersion }, null, now));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) { throw Fail("REVISION_ALREADY_OPEN", "Sản phẩm đã có một bản chỉnh sửa đang mở."); }
        await tx.CommitAsync(ct); return ToDto(revision);
    }

    public async Task<ProductRevisionDto> UpdateAsync(Guid staffId, Guid revisionId, UpdateProductRevisionRequest request, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var revision = await LockDraftRevision(revisionId, ct);
        await ValidateTaxonomy(request.CategoryId, request.BrandId, ct);
        revision.Title = request.Title.Trim(); revision.Description = Clean(request.Description); revision.CategoryId = request.CategoryId; revision.BrandId = request.BrandId; revision.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct); await LoadChildren(revision, ct); await tx.CommitAsync(ct); return ToDto(revision);
    }

    public async Task<ProductRevisionDto> SubmitAsync(Guid staffId, Guid revisionId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); var revision = await LockDraftRevision(revisionId, ct);
        var product = await db.Products.SingleAsync(x => x.Id == revision.ProductId, ct);
        if (product.PublicationStatus is not ("PUBLISHED" or "SUSPENDED")) throw Fail("REVISION_INVALID_STATE", "Sản phẩm đã lưu trữ không thể gửi duyệt revision.");
        if (revision.BaseContentVersion != product.ContentVersion) throw Fail("REVISION_STALE", "Bản chỉnh sửa được tạo từ nội dung cũ; hãy tạo lại revision.");
        var now = DateTime.UtcNow; revision.RevisionStatus = "PENDING_REVIEW"; revision.SubmittedBy = staffId; revision.SubmittedAt = now; revision.UpdatedAt = now;
        db.AuditLogs.Add(Audit(staffId, "PRODUCT_REVISION_SUBMITTED", revision.Id, new { status = "DRAFT" }, new { status = revision.RevisionStatus }, null, now));
        await db.SaveChangesAsync(ct); await LoadChildren(revision, ct); await tx.CommitAsync(ct); return ToDto(revision);
    }

    public async Task<ProductRevisionDto> ApproveAsync(Guid adminId, Guid revisionId, RevisionDecisionRequest request, string correlationId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var probe = await db.ProductRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == revisionId, ct) ?? throw Fail("REVISION_NOT_FOUND", "Không tìm thấy bản chỉnh sửa.");
        var product = await LockProduct(probe.ProductId, ct); var revision = await LockRevision(revisionId, ct); await LoadChildren(revision, ct);
        if (revision.RevisionStatus != "PENDING_REVIEW") throw Fail("REVISION_INVALID_STATE", "Chỉ revision đang chờ duyệt mới được approve.");
        if (product.PublicationStatus is not ("PUBLISHED" or "SUSPENDED")) throw Fail("REVISION_INVALID_STATE", "Sản phẩm đã lưu trữ không thể duyệt revision.");
        if (revision.BaseContentVersion != product.ContentVersion) throw Fail("REVISION_STALE", "Nội dung công khai đã đổi sau khi revision được tạo.");
        await ValidateTaxonomy(revision.CategoryId, revision.BrandId, ct);
        var before = new { product.Title, product.Description, product.CategoryId, product.BrandId, product.ContentVersion };
        product.Title = revision.Title; product.Description = revision.Description; product.CategoryId = revision.CategoryId; product.BrandId = revision.BrandId; product.ContentVersion++; product.UpdatedAt = DateTime.UtcNow;
        await approvalCheckpoint.ReachAsync(ProductRevisionApprovalStage.AfterScalarMutation, ct);
        await ApplyImages(product.Id, revision, ct);
        await approvalCheckpoint.ReachAsync(ProductRevisionApprovalStage.AfterImageMutation, ct);
        await ApplySpecifications(product.Id, revision, ct);
        await approvalCheckpoint.ReachAsync(ProductRevisionApprovalStage.AfterSpecificationMutation, ct);
        var now = DateTime.UtcNow; revision.RevisionStatus = "APPROVED"; revision.ReviewedBy = adminId; revision.ReviewedAt = now; revision.ReviewNote = Clean(request.Note); revision.UpdatedAt = now;
        db.AuditLogs.Add(Audit(adminId, "PRODUCT_REVISION_APPROVED", revision.Id, before, new { product.Title, product.Description, product.CategoryId, product.BrandId, product.ContentVersion, revisionId }, correlationId, now));
        await db.SaveChangesAsync(ct);
        await approvalCheckpoint.ReachAsync(ProductRevisionApprovalStage.AfterSaveBeforeCommit, ct);
        await tx.CommitAsync(ct); return ToDto(revision);
    }

    public Task<ProductRevisionDto> RejectAsync(Guid adminId, Guid revisionId, RejectProductRevisionRequest request, string correlationId, CancellationToken ct = default) =>
        TerminalDecision(adminId, revisionId, "PENDING_REVIEW", "REJECTED", request.Reason, "PRODUCT_REVISION_REJECTED", correlationId, ct);

    public Task<ProductRevisionDto> CancelAsync(Guid staffId, Guid revisionId, RevisionDecisionRequest request, CancellationToken ct = default) =>
        TerminalDecision(staffId, revisionId, null, "CANCELLED", request.Note, "PRODUCT_REVISION_CANCELLED", null, ct);

    public async Task<ProductRevisionSpecificationDto> UpsertSpecificationAsync(Guid staffId, Guid revisionId, string key, UpsertRevisionSpecificationRequest request, CancellationToken ct = default)
    {
        var normalized = key.Trim().ToLowerInvariant(); if (!Regex.IsMatch(normalized, "^[a-z0-9]+(?:_[a-z0-9]+)*$") || normalized.Length > 80) throw Fail("VALIDATION_ERROR", "Khóa thông số phải là snake_case.");
        await using var tx = await db.Database.BeginTransactionAsync(ct); var revision = await LockDraftRevision(revisionId, ct);
        var spec = await db.ProductRevisionSpecifications.SingleOrDefaultAsync(x => x.RevisionId == revision.Id && x.SpecKey == normalized, ct); var now = DateTime.UtcNow;
        if (spec is null) { spec = new ProductRevisionSpecification { Id = Guid.NewGuid(), RevisionId = revision.Id, SpecKey = normalized, CreatedAt = now }; db.ProductRevisionSpecifications.Add(spec); }
        spec.DisplayName = request.DisplayName.Trim(); spec.SpecValue = request.Value.Trim(); spec.DisplayOrder = request.DisplayOrder; spec.UpdatedAt = now;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return SpecDto(spec);
    }

    public async Task DeleteSpecificationAsync(Guid staffId, Guid revisionId, string key, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); var revision = await LockDraftRevision(revisionId, ct);
        var spec = await db.ProductRevisionSpecifications.SingleOrDefaultAsync(x => x.RevisionId == revision.Id && x.SpecKey == key.Trim().ToLowerInvariant(), ct) ?? throw Fail("REVISION_NOT_FOUND", "Không tìm thấy thông số revision.");
        db.Remove(spec); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    public async Task<ProductRevisionImageDto> UploadImageAsync(Guid staffId, Guid revisionId, Stream content, string fileName, string contentType, long length, string? altText, int displayOrder, CancellationToken ct = default)
    {
        var probe = await db.ProductRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == revisionId, ct) ?? throw Fail("REVISION_NOT_FOUND", "Không tìm thấy revision.");
        var stored = await storage.StoreAsync(probe.ProductId, content, fileName, contentType, length, ct);
        try { await using var tx = await db.Database.BeginTransactionAsync(ct); var revision = await LockDraftRevision(revisionId, ct); var now = DateTime.UtcNow; var image = new ProductRevisionImage { Id = Guid.NewGuid(), RevisionId = revision.Id, FileKey = stored.PublicPath, AltText = Clean(altText), DisplayOrder = displayOrder, ImageStatus = "ACTIVE", IsNewUpload = true, CreatedAt = now, UpdatedAt = now }; db.Add(image); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return ImageDto(image); }
        catch
        {
            try { await storage.DeleteAsync(stored.StorageKey, CancellationToken.None); }
            catch (Exception cleanupError) { await EnqueueUploadCompensation(probe.ProductId, revisionId, stored.StorageKey, stored.PublicPath, cleanupError); }
            throw;
        }
    }

    public async Task<ProductRevisionImageDto> UpdateImageAsync(Guid staffId, Guid revisionId, Guid imageId, UpdateRevisionImageRequest request, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); var revision = await LockDraftRevision(revisionId, ct); var image = await db.ProductRevisionImages.SingleOrDefaultAsync(x => x.RevisionId == revision.Id && x.Id == imageId, ct) ?? throw Fail("REVISION_NOT_FOUND", "Không tìm thấy ảnh revision."); image.AltText = Clean(request.AltText); image.DisplayOrder = request.DisplayOrder; image.UpdatedAt = DateTime.UtcNow; await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return ImageDto(image);
    }

    public async Task<IReadOnlyList<ProductRevisionImageDto>> ReorderImagesAsync(Guid staffId, Guid revisionId, ReorderRevisionImagesRequest request, CancellationToken ct = default)
    {
        if (request.ImageIds.Count != request.ImageIds.Distinct().Count()) throw Fail("VALIDATION_ERROR", "Danh sách ảnh có ID trùng.");
        await using var tx = await db.Database.BeginTransactionAsync(ct); var revision = await LockDraftRevision(revisionId, ct); var images = await db.ProductRevisionImages.Where(x => x.RevisionId == revision.Id).ToListAsync(ct);
        if (images.Count != request.ImageIds.Count || images.Any(x => !request.ImageIds.Contains(x.Id))) throw Fail("REVISION_SNAPSHOT_INVALID", "Gallery đã thay đổi; vui lòng tải lại.");
        var order = request.ImageIds.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index); foreach (var image in images) { image.DisplayOrder = order[image.Id]; image.UpdatedAt = DateTime.UtcNow; }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return images.OrderBy(x => x.DisplayOrder).Select(ImageDto).ToList();
    }

    public async Task DeleteImageAsync(Guid staffId, Guid revisionId, Guid imageId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); var revision = await LockDraftRevision(revisionId, ct); var image = await db.ProductRevisionImages.SingleOrDefaultAsync(x => x.RevisionId == revision.Id && x.Id == imageId, ct) ?? throw Fail("REVISION_NOT_FOUND", "Không tìm thấy ảnh revision.");
        if (image.IsNewUpload) EnqueueCleanup(revision.ProductId, revision.Id, image.FileKey, "REVISION_IMAGE_DELETED");
        db.Remove(image); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    private async Task<ProductRevisionDto> TerminalDecision(Guid actorId, Guid revisionId, string? requiredStatus, string target, string? note, string action, string? correlationId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); var probe = await db.ProductRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == revisionId, ct) ?? throw Fail("REVISION_NOT_FOUND", "Không tìm thấy revision."); await LockProduct(probe.ProductId, ct); var revision = await LockRevision(revisionId, ct); await LoadChildren(revision, ct);
        if (requiredStatus is not null ? revision.RevisionStatus != requiredStatus : revision.RevisionStatus is not ("DRAFT" or "PENDING_REVIEW")) throw Fail("REVISION_INVALID_STATE", "Revision không còn ở trạng thái cho phép.");
        var before = revision.RevisionStatus; var now = DateTime.UtcNow; revision.RevisionStatus = target; revision.ReviewedBy = actorId; revision.ReviewedAt = now; revision.ReviewNote = Clean(note); revision.UpdatedAt = now;
        var cleanupReason = target == "REJECTED" ? "REVISION_REJECTED" : "REVISION_CANCELLED";
        foreach (var image in revision.Images.Where(x => x.IsNewUpload)) EnqueueCleanup(revision.ProductId, revision.Id, image.FileKey, cleanupReason);
        db.AuditLogs.Add(Audit(actorId, action, revision.Id, new { status = before }, new { status = target, note = revision.ReviewNote }, correlationId, now)); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return ToDto(revision);
    }

    private async Task ApplyImages(Guid productId, ProductRevision revision, CancellationToken ct)
    {
        var live = await db.ProductImages.Where(x => x.ProductId == productId).ToListAsync(ct); var desiredSourceIds = revision.Images.Where(x => x.SourceImageId.HasValue).Select(x => x.SourceImageId!.Value).ToHashSet();
        db.ProductImages.RemoveRange(live.Where(x => !desiredSourceIds.Contains(x.Id)));
        foreach (var snapshot in revision.Images.OrderBy(x => x.DisplayOrder).ThenBy(x => x.Id))
        {
            var target = snapshot.SourceImageId.HasValue ? live.SingleOrDefault(x => x.Id == snapshot.SourceImageId.Value) : null;
            if (target is null) { target = new ProductImage { Id = Guid.NewGuid(), ProductId = productId, CreatedAt = DateTime.UtcNow }; db.ProductImages.Add(target); snapshot.SourceImageId = target.Id; snapshot.IsNewUpload = false; }
            target.FileKey = snapshot.FileKey; target.AltText = snapshot.AltText; target.DisplayOrder = snapshot.DisplayOrder; target.ImageStatus = snapshot.ImageStatus; target.UpdatedAt = DateTime.UtcNow;
        }
    }

    private async Task ApplySpecifications(Guid productId, ProductRevision revision, CancellationToken ct)
    {
        var live = await db.ProductSpecifications.Where(x => x.ProductId == productId).ToListAsync(ct); var keys = revision.Specifications.Select(x => x.SpecKey).ToHashSet(); db.ProductSpecifications.RemoveRange(live.Where(x => !keys.Contains(x.SpecKey)));
        foreach (var snapshot in revision.Specifications) { var target = live.SingleOrDefault(x => x.SpecKey == snapshot.SpecKey); if (target is null) { target = new ProductSpecification { Id = Guid.NewGuid(), ProductId = productId, SpecKey = snapshot.SpecKey, CreatedAt = DateTime.UtcNow }; db.Add(target); } target.DisplayName = snapshot.DisplayName; target.SpecValue = snapshot.SpecValue; target.DisplayOrder = snapshot.DisplayOrder; target.UpdatedAt = DateTime.UtcNow; }
    }

    private IQueryable<ProductRevision> RevisionQuery() => db.ProductRevisions.Include(x => x.Images).Include(x => x.Specifications);
    private async Task<Product> LockProduct(Guid id, CancellationToken ct) => await db.Products.FromSqlInterpolated($"SELECT * FROM store.products WHERE id={id} FOR UPDATE").SingleOrDefaultAsync(ct) ?? throw Fail("REVISION_NOT_FOUND", "Không tìm thấy sản phẩm.");
    private async Task<ProductRevision> LockRevision(Guid id, CancellationToken ct) => await db.ProductRevisions.FromSqlInterpolated($"SELECT * FROM store.product_revisions WHERE id={id} FOR UPDATE").SingleOrDefaultAsync(ct) ?? throw Fail("REVISION_NOT_FOUND", "Không tìm thấy revision.");
    private async Task<ProductRevision> LockDraftRevision(Guid id, CancellationToken ct) { var probe = await db.ProductRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Fail("REVISION_NOT_FOUND", "Không tìm thấy revision."); await LockProduct(probe.ProductId, ct); var revision = await LockRevision(id, ct); if (revision.RevisionStatus != "DRAFT") throw Fail("REVISION_INVALID_STATE", "Chỉ revision DRAFT mới được chỉnh sửa."); return revision; }
    private async Task LoadChildren(ProductRevision revision, CancellationToken ct) { await db.Entry(revision).Collection(x => x.Images).LoadAsync(ct); await db.Entry(revision).Collection(x => x.Specifications).LoadAsync(ct); }
    private async Task ValidateTaxonomy(Guid? categoryId, Guid? brandId, CancellationToken ct) { if (categoryId.HasValue && !await db.Categories.AnyAsync(x => x.Id == categoryId, ct)) throw Fail("REVISION_CATEGORY_NOT_FOUND", "Danh mục không tồn tại."); if (brandId.HasValue && !await db.Brands.AnyAsync(x => x.Id == brandId && x.IsActive, ct)) throw Fail("REVISION_BRAND_INACTIVE", "Thương hiệu không tồn tại hoặc đã ngừng dùng."); }
    private async Task SafeDelete(string key) { try { await storage.DeleteAsync(key, CancellationToken.None); } catch (Exception ex) { logger.LogWarning(ex, "Cannot cleanup revision media {StorageKey}", key); } }
    private void EnqueueCleanup(Guid productId, Guid revisionId, string publicPath, string reason)
    {
        if (!storage.TryGetStorageKey(publicPath, out var key)) return;
        var now = DateTime.UtcNow;
        db.ProductMediaCleanupJobs.Add(new ProductMediaCleanupJob { Id = Guid.NewGuid(), ProductId = productId, RevisionId = revisionId, StorageKey = key, PublicPath = publicPath, CleanupReason = reason, JobStatus = "PENDING", NextAttemptAt = now, CreatedAt = now, UpdatedAt = now });
    }
    private async Task EnqueueUploadCompensation(Guid productId, Guid revisionId, string storageKey, string publicPath, Exception cleanupError)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var cleanupDb = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            var id = Guid.NewGuid(); var now = DateTime.UtcNow;
            await cleanupDb.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO store.product_media_cleanup_jobs(id,product_id,revision_id,storage_key,public_path,cleanup_reason,job_status,next_attempt_at,created_at,updated_at)
                VALUES({{id}},{{productId}},{{revisionId}},{{storageKey}},{{publicPath}},'UPLOAD_COMPENSATION','PENDING',{{now}},{{now}},{{now}})
                ON CONFLICT DO NOTHING
                """);
            logger.LogWarning(cleanupError, "Immediate revision upload cleanup failed; queued {StorageKey} for retry.", storageKey);
        }
        catch (Exception queueError)
        {
            logger.LogError(queueError, "Cannot persist upload compensation job for {StorageKey}.", storageKey);
        }
    }
    private static ProductRevisionDto ToDto(ProductRevision x) => new(x.Id, x.ProductId, x.RevisionNo, x.RevisionStatus, x.BaseContentVersion, x.Title, x.Description, x.CategoryId, x.BrandId, x.CreatedBy, x.CreatedAt, x.SubmittedAt, x.ReviewedAt, x.ReviewNote, x.Images.OrderBy(i => i.DisplayOrder).ThenBy(i => i.Id).Select(ImageDto).ToList(), x.Specifications.OrderBy(s => s.DisplayOrder).ThenBy(s => s.SpecKey).Select(SpecDto).ToList());
    private static ProductRevisionImageDto ImageDto(ProductRevisionImage x) => new(x.Id, x.SourceImageId, x.FileKey, x.AltText, x.DisplayOrder, x.ImageStatus, x.IsNewUpload);
    private static ProductRevisionSpecificationDto SpecDto(ProductRevisionSpecification x) => new(x.Id, x.SpecKey, x.DisplayName, x.SpecValue, x.DisplayOrder);
    private static ProductRevisionFailure Fail(string code, string message) => new(code, message);
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static readonly HashSet<string> Statuses = ["DRAFT", "PENDING_REVIEW", "APPROVED", "REJECTED", "CANCELLED"];
    private static AuditLog Audit(Guid actor, string action, Guid entityId, object? before, object? after, string? correlationId, DateTime now) => new() { Id = Guid.NewGuid(), ActorId = actor, ActorType = "USER", Action = action, EntityType = "ProductRevision", EntityId = entityId, BeforeData = before is null ? null : JsonSerializer.Serialize(before), AfterData = after is null ? null : JsonSerializer.Serialize(after), CorrelationId = correlationId, OccurredAt = now, CreatedAt = now, UpdatedAt = now };
}
