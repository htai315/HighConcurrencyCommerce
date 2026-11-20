using System.ComponentModel.DataAnnotations;

namespace HighConcurrencyCommerce.Application.ProductRevisions;

public sealed class UpdateProductRevisionRequest
{
    [Required, StringLength(240, MinimumLength = 2)] public string Title { get; init; } = string.Empty;
    [StringLength(5000)] public string? Description { get; init; }
    public Guid? CategoryId { get; init; }
    public Guid? BrandId { get; init; }
}

public sealed class RevisionDecisionRequest
{
    [StringLength(1000)] public string? Note { get; init; }
}

public sealed class RejectProductRevisionRequest
{
    [Required, StringLength(1000, MinimumLength = 3)] public string Reason { get; init; } = string.Empty;
}

public sealed class UpsertRevisionSpecificationRequest
{
    [Required, StringLength(160)] public string DisplayName { get; init; } = string.Empty;
    [Required, StringLength(500)] public string Value { get; init; } = string.Empty;
    [Range(0, 10000)] public int DisplayOrder { get; init; }
}

public sealed class UpdateRevisionImageRequest
{
    [StringLength(240)] public string? AltText { get; init; }
    [Range(0, 10000)] public int DisplayOrder { get; init; }
}

public sealed class ReorderRevisionImagesRequest
{
    [Required, MinLength(1), MaxLength(100)] public IReadOnlyList<Guid> ImageIds { get; init; } = [];
}

public sealed record ProductRevisionImageDto(Guid Id, Guid? SourceImageId, string FileKey, string? AltText, int DisplayOrder, string ImageStatus, bool IsNewUpload);
public sealed record ProductRevisionSpecificationDto(Guid Id, string Key, string DisplayName, string Value, int DisplayOrder);
public sealed record ProductRevisionSummaryDto(Guid Id, Guid ProductId, int RevisionNo, string RevisionStatus, string ProductTitle, long BaseContentVersion, DateTime CreatedAt, DateTime? SubmittedAt, string? ReviewNote);
public sealed record ProductRevisionDto(Guid Id, Guid ProductId, int RevisionNo, string RevisionStatus, long BaseContentVersion, string Title, string? Description, Guid? CategoryId, Guid? BrandId, Guid CreatedBy, DateTime CreatedAt, DateTime? SubmittedAt, DateTime? ReviewedAt, string? ReviewNote, IReadOnlyList<ProductRevisionImageDto> Images, IReadOnlyList<ProductRevisionSpecificationDto> Specifications);
public sealed record ProductRevisionFieldDiffDto(string Field, string Label, string? CurrentValue, string? ProposedValue, bool Changed);
public sealed record ProductRevisionImageDiffDto(Guid? LiveImageId, Guid? RevisionImageId, string ChangeType, string? CurrentFileKey, string? ProposedFileKey, string? CurrentAltText, string? ProposedAltText, int? CurrentDisplayOrder, int? ProposedDisplayOrder, string? CleanupStatus, bool PreviewAvailable);
public sealed record ProductRevisionSpecificationDiffDto(string Key, string ChangeType, string? CurrentDisplayName, string? ProposedDisplayName, string? CurrentValue, string? ProposedValue, int? CurrentDisplayOrder, int? ProposedDisplayOrder);
public sealed record ProductRevisionReviewDto(Guid RevisionId, Guid ProductId, int RevisionNo, string RevisionStatus, string ProductStatus, long BaseContentVersion, long CurrentContentVersion, bool IsStale, bool CanApprove, IReadOnlyList<ProductRevisionFieldDiffDto> Content, IReadOnlyList<ProductRevisionImageDiffDto> Images, IReadOnlyList<ProductRevisionSpecificationDiffDto> Specifications);

public enum ProductRevisionApprovalStage
{
    AfterScalarMutation,
    AfterImageMutation,
    AfterSpecificationMutation,
    AfterSaveBeforeCommit
}

public sealed class ProductRevisionFailure(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface IProductRevisionService
{
    Task<ProductRevisionDto?> GetOpenAsync(Guid productId, CancellationToken ct = default);
    Task<ProductRevisionDto> GetAsync(Guid revisionId, CancellationToken ct = default);
    Task<IReadOnlyList<ProductRevisionSummaryDto>> GetQueueAsync(string? status, CancellationToken ct = default);
    Task<ProductRevisionDto> CreateAsync(Guid staffId, Guid productId, CancellationToken ct = default);
    Task<ProductRevisionDto> UpdateAsync(Guid staffId, Guid revisionId, UpdateProductRevisionRequest request, CancellationToken ct = default);
    Task<ProductRevisionDto> SubmitAsync(Guid staffId, Guid revisionId, CancellationToken ct = default);
    Task<ProductRevisionDto> CancelAsync(Guid staffId, Guid revisionId, RevisionDecisionRequest request, CancellationToken ct = default);
    Task<ProductRevisionDto> ApproveAsync(Guid adminId, Guid revisionId, RevisionDecisionRequest request, string correlationId, CancellationToken ct = default);
    Task<ProductRevisionDto> RejectAsync(Guid adminId, Guid revisionId, RejectProductRevisionRequest request, string correlationId, CancellationToken ct = default);
    Task<ProductRevisionSpecificationDto> UpsertSpecificationAsync(Guid staffId, Guid revisionId, string key, UpsertRevisionSpecificationRequest request, CancellationToken ct = default);
    Task DeleteSpecificationAsync(Guid staffId, Guid revisionId, string key, CancellationToken ct = default);
    Task<ProductRevisionImageDto> UploadImageAsync(Guid staffId, Guid revisionId, Stream content, string fileName, string contentType, long length, string? altText, int displayOrder, CancellationToken ct = default);
    Task<ProductRevisionImageDto> UpdateImageAsync(Guid staffId, Guid revisionId, Guid imageId, UpdateRevisionImageRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<ProductRevisionImageDto>> ReorderImagesAsync(Guid staffId, Guid revisionId, ReorderRevisionImagesRequest request, CancellationToken ct = default);
    Task DeleteImageAsync(Guid staffId, Guid revisionId, Guid imageId, CancellationToken ct = default);
}

public interface IProductRevisionReviewReader
{
    Task<ProductRevisionReviewDto> GetReviewAsync(Guid revisionId, CancellationToken ct = default);
}

public interface IProductRevisionApprovalCheckpoint
{
    Task ReachAsync(ProductRevisionApprovalStage stage, CancellationToken ct = default);
}
