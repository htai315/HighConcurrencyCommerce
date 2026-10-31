using System.ComponentModel.DataAnnotations;

namespace HighConcurrencyCommerce.Application.FlashSale;

public sealed class CreateFlashSaleCampaignRequest
{
    [Required(ErrorMessage = "Tên chiến dịch không được để trống.")]
    [StringLength(240, MinimumLength = 1, ErrorMessage = "Tên chiến dịch từ 1 đến 240 ký tự.")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Thời gian bắt đầu là bắt buộc.")]
    public DateTime StartsAt { get; set; }

    [Required(ErrorMessage = "Thời gian kết thúc là bắt buộc.")]
    public DateTime EndsAt { get; set; }
}

public sealed class UpdateFlashSaleCampaignRequest
{
    [Required(ErrorMessage = "Tên chiến dịch không được để trống.")]
    [StringLength(240, MinimumLength = 1, ErrorMessage = "Tên chiến dịch từ 1 đến 240 ký tự.")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Thời gian bắt đầu là bắt buộc.")]
    public DateTime StartsAt { get; set; }

    [Required(ErrorMessage = "Thời gian kết thúc là bắt buộc.")]
    public DateTime EndsAt { get; set; }
}

public sealed class CreateFlashSaleOfferRequest
{
    [Required(ErrorMessage = "Mã biến thể SKU không được để trống.")]
    public Guid ProductSkuId { get; set; }

    [Range(typeof(decimal), "0.01", "9999999999999999.99", ErrorMessage = "Giá khuyến mãi phải lớn hơn 0.")]
    public decimal SalePrice { get; set; }

    [Range(1, 1000000, ErrorMessage = "Số lượng quota phải từ 1 đến 1.000.000.")]
    public int QuotaTotal { get; set; }

    [Range(1, 99, ErrorMessage = "Giới hạn mỗi khách hàng phải từ 1 đến 99.")]
    public short PerCustomerLimit { get; set; } = 2;
}

public sealed class CancelFlashSaleCampaignRequest
{
    [StringLength(500, ErrorMessage = "Lý do hủy tối đa 500 ký tự.")]
    public string? Reason { get; set; }
}

public sealed record FlashSaleOfferDto(
    Guid Id,
    Guid CampaignId,
    Guid ProductSkuId,
    Guid ProductId,
    string ProductTitle,
    string SkuCode,
    string OptionLabel,
    decimal RegularPrice,
    decimal SalePrice,
    int QuotaTotal,
    int QuotaHeld,
    int QuotaSold,
    int RemainingQuota,
    short PerCustomerLimit);

public sealed record FlashSaleCampaignSummaryDto(
    Guid Id,
    string Name,
    DateTime StartsAt,
    DateTime EndsAt,
    string CampaignStatus,
    Guid CreatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int OffersCount,
    int TotalQuota,
    int TotalSold);

public sealed record FlashSaleCampaignDetailDto(
    Guid Id,
    string Name,
    DateTime StartsAt,
    DateTime EndsAt,
    string CampaignStatus,
    Guid CreatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<FlashSaleOfferDto> Offers);

public sealed record FlashSaleOfferPerformanceDto(
    Guid OfferId,
    string SkuCode,
    string ProductTitle,
    decimal SalePrice,
    int QuotaTotal,
    int QuotaHeld,
    int QuotaSold,
    int RemainingQuota,
    decimal SoldRevenue);

public sealed record FlashSalePerformanceDto(
    Guid CampaignId,
    string Name,
    string Status,
    DateTime StartsAt,
    DateTime EndsAt,
    int TotalQuota,
    int TotalHeld,
    int TotalSold,
    int RemainingQuota,
    decimal EstimatedRevenue,
    IReadOnlyList<FlashSaleOfferPerformanceDto> Offers);

public sealed record PublicFlashSaleOfferDto(
    Guid OfferId,
    Guid ProductSkuId,
    Guid ProductId,
    string ProductTitle,
    string SkuCode,
    string OptionLabel,
    decimal RegularPrice,
    decimal SalePrice,
    int DiscountPercentage,
    int QuotaTotal,
    int RemainingQuota,
    short PerCustomerLimit,
    string? ImageUrl);

public sealed record PublicFlashSaleCampaignDto(
    Guid Id,
    string Name,
    DateTime StartsAt,
    DateTime EndsAt,
    string CampaignStatus,
    IReadOnlyList<PublicFlashSaleOfferDto> Offers);

public sealed class FlashSaleFailure(string code, string message = "") : Exception(message)
{
    public string Code { get; } = code;
}

public interface IFlashSaleService
{
    Task<IReadOnlyList<FlashSaleCampaignSummaryDto>> GetAdminCampaignsAsync(CancellationToken ct = default);
    Task<FlashSaleCampaignDetailDto> GetAdminCampaignByIdAsync(Guid id, CancellationToken ct = default);
    Task<FlashSaleCampaignDetailDto> CreateCampaignAsync(Guid adminId, CreateFlashSaleCampaignRequest request, string correlationId, CancellationToken ct = default);
    Task<FlashSaleCampaignDetailDto> UpdateCampaignAsync(Guid adminId, Guid id, UpdateFlashSaleCampaignRequest request, string correlationId, CancellationToken ct = default);
    Task<FlashSaleOfferDto> AddOfferAsync(Guid adminId, Guid campaignId, CreateFlashSaleOfferRequest request, string correlationId, CancellationToken ct = default);
    Task DeleteOfferAsync(Guid adminId, Guid campaignId, Guid offerId, string correlationId, CancellationToken ct = default);
    Task<FlashSaleCampaignDetailDto> PublishCampaignAsync(Guid adminId, Guid campaignId, string correlationId, CancellationToken ct = default);
    Task<FlashSaleCampaignDetailDto> CancelCampaignAsync(Guid adminId, Guid campaignId, CancelFlashSaleCampaignRequest? request, string correlationId, CancellationToken ct = default);
    Task<FlashSalePerformanceDto> GetCampaignPerformanceAsync(Guid campaignId, CancellationToken ct = default);
    Task<IReadOnlyList<PublicFlashSaleCampaignDto>> GetPublicActiveCampaignsAsync(CancellationToken ct = default);
    Task<PublicFlashSaleCampaignDto?> GetPublicCampaignByIdAsync(Guid id, CancellationToken ct = default);
    Task<int> SynchronizeLifecycleAsync(int batchSize = 100, CancellationToken ct = default);
}

