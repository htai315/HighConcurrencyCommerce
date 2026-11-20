using HighConcurrencyCommerce.Application.ProductRevisions;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace HighConcurrencyCommerce.Infrastructure.ProductRevisions;

public sealed class PostgresProductRevisionReviewReader(StoreDbContext db) : IProductRevisionReviewReader
{
    public async Task<ProductRevisionReviewDto> GetReviewAsync(Guid revisionId, CancellationToken ct = default)
    {
        var revision = await db.ProductRevisions.AsNoTracking()
            .Include(x => x.Images).Include(x => x.Specifications)
            .SingleOrDefaultAsync(x => x.Id == revisionId, ct)
            ?? throw new ProductRevisionFailure("REVISION_NOT_FOUND", "Không tìm thấy bản chỉnh sửa sản phẩm.");
        var product = await db.Products.AsNoTracking()
            .Include(x => x.Category).Include(x => x.Brand)
            .Include(x => x.ProductImages).Include(x => x.ProductSpecifications)
            .SingleAsync(x => x.Id == revision.ProductId, ct);

        var proposedCategory = revision.CategoryId.HasValue
            ? await db.Categories.AsNoTracking().Where(x => x.Id == revision.CategoryId).Select(x => x.Name).SingleOrDefaultAsync(ct)
            : null;
        var proposedBrand = revision.BrandId.HasValue
            ? await db.Brands.AsNoTracking().Where(x => x.Id == revision.BrandId).Select(x => x.Name).SingleOrDefaultAsync(ct)
            : null;
        var cleanup = await db.ProductMediaCleanupJobs.AsNoTracking()
            .Where(x => x.RevisionId == revision.Id)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);
        var cleanupByPath = cleanup.GroupBy(x => x.PublicPath ?? string.Empty).ToDictionary(x => x.Key, x => x.First().JobStatus);

        var content = new[]
        {
            Field("title", "Tiêu đề", product.Title, revision.Title),
            Field("description", "Mô tả", product.Description, revision.Description),
            Field("category", "Danh mục", product.Category?.Name, proposedCategory),
            Field("brand", "Thương hiệu", product.Brand?.Name, proposedBrand)
        };

        var liveById = product.ProductImages.ToDictionary(x => x.Id);
        var proposedSourceIds = revision.Images.Where(x => x.SourceImageId.HasValue).Select(x => x.SourceImageId!.Value).ToHashSet();
        var imageDiffs = new List<ProductRevisionImageDiffDto>();
        foreach (var proposed in revision.Images.OrderBy(x => x.DisplayOrder).ThenBy(x => x.Id))
        {
            var live = proposed.SourceImageId.HasValue && liveById.TryGetValue(proposed.SourceImageId.Value, out var source) ? source : null;
            var type = live is null ? "ADDED" : ImageMetadataChanged(live, proposed) ? "MODIFIED" : live.DisplayOrder != proposed.DisplayOrder ? "REORDERED" : "UNCHANGED";
            var status = Cleanup(cleanupByPath, proposed.FileKey);
            imageDiffs.Add(new(live?.Id, proposed.Id, type, live?.FileKey, proposed.FileKey, live?.AltText, proposed.AltText, live?.DisplayOrder, proposed.DisplayOrder, status, status != "COMPLETED"));
        }
        foreach (var live in product.ProductImages.Where(x => !proposedSourceIds.Contains(x.Id)).OrderBy(x => x.DisplayOrder).ThenBy(x => x.Id))
        {
            imageDiffs.Add(new(live.Id, null, "REMOVED", live.FileKey, null, live.AltText, null, live.DisplayOrder, null, null, true));
        }

        var liveSpecs = product.ProductSpecifications.ToDictionary(x => x.SpecKey);
        var proposedSpecs = revision.Specifications.ToDictionary(x => x.SpecKey);
        var specificationDiffs = liveSpecs.Keys.Union(proposedSpecs.Keys).OrderBy(x => x).Select(key =>
        {
            liveSpecs.TryGetValue(key, out var live); proposedSpecs.TryGetValue(key, out var proposed);
            var type = live is null ? "ADDED" : proposed is null ? "REMOVED" : SpecChanged(live, proposed) ? "MODIFIED" : "UNCHANGED";
            return new ProductRevisionSpecificationDiffDto(key, type, live?.DisplayName, proposed?.DisplayName, live?.SpecValue, proposed?.SpecValue, live?.DisplayOrder, proposed?.DisplayOrder);
        }).ToList();

        var stale = revision.BaseContentVersion != product.ContentVersion;
        var canApprove = revision.RevisionStatus == "PENDING_REVIEW" && !stale && product.PublicationStatus is "PUBLISHED" or "SUSPENDED";
        return new(revision.Id, product.Id, revision.RevisionNo, revision.RevisionStatus, product.PublicationStatus, revision.BaseContentVersion, product.ContentVersion, stale, canApprove, content, imageDiffs, specificationDiffs);
    }

    private static ProductRevisionFieldDiffDto Field(string field, string label, string? current, string? proposed) =>
        new(field, label, current, proposed, !string.Equals(current, proposed, StringComparison.Ordinal));
    private static bool ImageMetadataChanged(ProductImage x, ProductRevisionImage y) =>
        x.FileKey != y.FileKey || x.AltText != y.AltText || x.ImageStatus != y.ImageStatus;
    private static bool SpecChanged(ProductSpecification x, ProductRevisionSpecification y) =>
        x.DisplayName != y.DisplayName || x.SpecValue != y.SpecValue || x.DisplayOrder != y.DisplayOrder;
    private static string? Cleanup(IReadOnlyDictionary<string, string> jobs, string path) => jobs.TryGetValue(path, out var status) ? status : null;
}
