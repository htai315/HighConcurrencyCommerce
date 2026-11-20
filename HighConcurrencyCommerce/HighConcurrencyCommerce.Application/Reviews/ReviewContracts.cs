using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace HighConcurrencyCommerce.Application.Reviews;

public sealed record RatingDistributionDto(
    int One,
    int Two,
    int Three,
    int Four,
    int Five);

public sealed record ProductRatingSummaryDto(
    decimal AverageRating,
    int ReviewCount,
    RatingDistributionDto Distribution);

public sealed record PublicProductReviewDto(
    Guid Id,
    short Rating,
    string Content,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string ReviewerDisplayName,
    bool VerifiedPurchase);

public sealed record ProductReviewPageDto(
    IReadOnlyList<PublicProductReviewDto> Items,
    ProductRatingSummaryDto Summary,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record MyProductReviewDto(
    Guid Id,
    Guid ProductId,
    string ProductTitle,
    short Rating,
    string Content,
    string ReviewStatus,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ModeratedAt,
    string? ModerationReason);

public sealed record ReviewEligibilityDto(
    bool Eligible,
    string? ReasonCode,
    MyProductReviewDto? ExistingReview);

public sealed class CreateProductReviewRequest
{
    [Range(1, 5, ErrorMessage = "Điểm đánh giá phải từ 1 đến 5 sao.")]
    public short Rating { get; set; }

    [Required(ErrorMessage = "Nội dung nhận xét không được để trống.")]
    [StringLength(2000, MinimumLength = 10, ErrorMessage = "Nội dung nhận xét phải từ 10 đến 2.000 ký tự.")]
    public string Content { get; set; } = string.Empty;
}

public sealed class UpdateProductReviewRequest
{
    [Range(1, 5, ErrorMessage = "Điểm đánh giá phải từ 1 đến 5 sao.")]
    public short Rating { get; set; }

    [Required(ErrorMessage = "Nội dung nhận xét không được để trống.")]
    [StringLength(2000, MinimumLength = 10, ErrorMessage = "Nội dung nhận xét phải từ 10 đến 2.000 ký tự.")]
    public string Content { get; set; } = string.Empty;
}

public sealed class ModerateProductReviewRequest
{
    [Required(ErrorMessage = "Hành động kiểm duyệt không được để trống.")]
    [RegularExpression("^(HIDE|RESTORE)$", ErrorMessage = "Hành động chỉ có thể là HIDE hoặc RESTORE.")]
    public string Action { get; set; } = string.Empty;

    [StringLength(500, ErrorMessage = "Lý do kiểm duyệt tối đa 500 ký tự.")]
    public string? Reason { get; set; }
}

public sealed record ReviewQuery
{
    [Range(1, 5)]
    public short? Rating { get; init; }

    [Range(1, 100_000, ErrorMessage = "Trang phải từ 1 đến 100.000.")]
    public int? Page { get; init; }

    [Range(1, 100, ErrorMessage = "Kích thước trang phải từ 1 đến 100.")]
    public int? PageSize { get; init; }

    [RegularExpression("^(newest|highest|lowest)$", ErrorMessage = "Kiểu sắp xếp chỉ có thể là newest, highest hoặc lowest.")]
    public string? Sort { get; init; }
}

public sealed record ReviewModerationQuery
{
    [RegularExpression("^(PUBLISHED|HIDDEN)$", ErrorMessage = "Trạng thái chỉ có thể là PUBLISHED hoặc HIDDEN.")]
    public string? Status { get; init; }

    [StringLength(100, ErrorMessage = "Từ khóa tìm kiếm tối đa 100 ký tự.")]
    public string? Search { get; init; }

    [Range(1, 100_000, ErrorMessage = "Trang phải từ 1 đến 100.000.")]
    public int? Page { get; init; }

    [Range(1, 100, ErrorMessage = "Kích thước trang phải từ 1 đến 100.")]
    public int? PageSize { get; init; }
}

public sealed record ModerationProductReviewDto(
    Guid Id,
    Guid ProductId,
    string ProductTitle,
    Guid CustomerId,
    string CustomerName,
    string CustomerEmail,
    short Rating,
    string Content,
    string ReviewStatus,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid? ModeratedBy,
    string? ModeratorName,
    DateTime? ModeratedAt,
    string? ModerationReason,
    DateTime? DeletedAt);

public sealed record ModerationProductReviewPageDto(
    IReadOnlyList<ModerationProductReviewDto> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed class ProductReviewFailure : Exception
{
    public string Code { get; }
    public int Status { get; }

    public ProductReviewFailure(string code, string message, int status = 400) : base(message)
    {
        Code = code;
        Status = status;
    }
}

public interface IProductReviewService
{
    Task<ReviewEligibilityDto> GetEligibilityAsync(Guid customerId, Guid productId, CancellationToken cancellationToken = default);
    Task<MyProductReviewDto> CreateReviewAsync(Guid customerId, Guid productId, CreateProductReviewRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MyProductReviewDto>> GetMyReviewsAsync(Guid customerId, CancellationToken cancellationToken = default);
    Task<MyProductReviewDto> UpdateReviewAsync(Guid customerId, Guid reviewId, UpdateProductReviewRequest request, CancellationToken cancellationToken = default);
    Task DeleteReviewAsync(Guid customerId, Guid reviewId, CancellationToken cancellationToken = default);
    Task<ProductReviewPageDto> GetPublicReviewsAsync(Guid productId, ReviewQuery query, CancellationToken cancellationToken = default);
    Task<ProductRatingSummaryDto> GetProductRatingSummaryAsync(Guid productId, CancellationToken cancellationToken = default);
    Task<ModerationProductReviewPageDto> GetModerationReviewsAsync(ReviewModerationQuery query, CancellationToken cancellationToken = default);
    Task<ModerationProductReviewDto> ModerateReviewAsync(Guid moderatorId, string moderatorRole, Guid reviewId, ModerateProductReviewRequest request, string? correlationId = null, CancellationToken cancellationToken = default);
}

