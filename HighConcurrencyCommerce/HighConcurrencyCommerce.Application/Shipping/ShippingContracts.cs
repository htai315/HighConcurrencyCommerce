using System.ComponentModel.DataAnnotations;

namespace HighConcurrencyCommerce.Application.Shipping;

public sealed class ShippingQuoteLineRequest
{
    [Required]
    public Guid ProductSkuId { get; set; }

    [Range(1, 99, ErrorMessage = "Số lượng mỗi SKU phải từ 1 đến 99.")]
    public int Quantity { get; set; }

    public Guid? FlashSaleOfferId { get; set; }
}

public sealed class ShippingQuoteRequest
{
    [Required]
    public Guid ShippingAddressId { get; set; }

    [Required, MinLength(1), MaxLength(50)]
    public List<ShippingQuoteLineRequest> Lines { get; set; } = [];
}

public sealed record ShippingQuoteDto(
    Guid ShippingMethodId,
    string Code,
    string Name,
    string? Description,
    decimal ShippingFee,
    decimal BaseFee,
    decimal ProvinceSurcharge,
    bool FreeShippingApplied,
    decimal? FreeShippingThreshold,
    short EstimatedMinDays,
    short EstimatedMaxDays,
    long MethodRevision,
    bool IsDefault);

public sealed record ShippingQuoteResultDto(
    Guid ShippingAddressId,
    string DestinationProvinceKey,
    decimal MerchandiseSubtotal,
    IReadOnlyList<ShippingQuoteDto> Quotes);

public sealed record ShippingFeeCalculationInput(
    decimal MerchandiseSubtotal,
    decimal BaseFee,
    decimal ProvinceSurcharge,
    decimal? FreeShippingThreshold);

public sealed record ShippingFeeCalculationResult(
    decimal ShippingFee,
    decimal FeeBeforeFreeShipping,
    bool FreeShippingApplied);

public interface IShippingFeeCalculator
{
    ShippingFeeCalculationResult Calculate(ShippingFeeCalculationInput input);
}

public interface IProvinceKeyNormalizer
{
    string Normalize(string province);
}

public interface IShippingQuoteService
{
    Task<ShippingQuoteResultDto> QuoteAsync(
        Guid customerId,
        ShippingQuoteRequest request,
        CancellationToken ct = default);
}

public sealed record AdminShippingMethodSummaryDto(
    Guid Id,
    string Code,
    string Name,
    string MethodStatus,
    decimal BaseFee,
    decimal? FreeShippingThreshold,
    short EstimatedMinDays,
    short EstimatedMaxDays,
    bool IsDefault,
    int DisplayOrder,
    long Revision,
    int ProvinceRateCount,
    DateTime UpdatedAt);

public sealed record AdminShippingProvinceRateDto(
    Guid Id,
    string ProvinceKey,
    decimal SurchargeFee,
    bool IsActive,
    DateTime UpdatedAt);

public sealed record AdminShippingMethodDetailDto(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    string MethodStatus,
    decimal BaseFee,
    decimal? FreeShippingThreshold,
    short EstimatedMinDays,
    short EstimatedMaxDays,
    bool IsDefault,
    int DisplayOrder,
    long Revision,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<AdminShippingProvinceRateDto> ProvinceRates);

public sealed class CreateShippingMethodRequest
{
    [Required, StringLength(32, MinimumLength = 3)]
    [RegularExpression("^[A-Za-z0-9_-]{3,32}$")]
    public string Code { get; init; } = string.Empty;

    [Required, StringLength(120, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; init; }

    [Range(typeof(decimal), "0", "9999999999999999")]
    public decimal BaseFee { get; init; }

    [Range(typeof(decimal), "1", "9999999999999999")]
    public decimal? FreeShippingThreshold { get; init; }

    [Range(0, 365)]
    public short EstimatedMinDays { get; init; }

    [Range(0, 365)]
    public short EstimatedMaxDays { get; init; }

    [Range(0, int.MaxValue)]
    public int DisplayOrder { get; init; }
}

public sealed class UpdateShippingMethodRequest
{
    [Required, StringLength(120, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; init; }

    [Range(typeof(decimal), "0", "9999999999999999")]
    public decimal BaseFee { get; init; }

    [Range(typeof(decimal), "1", "9999999999999999")]
    public decimal? FreeShippingThreshold { get; init; }

    [Range(0, 365)]
    public short EstimatedMinDays { get; init; }

    [Range(0, 365)]
    public short EstimatedMaxDays { get; init; }

    [Range(0, int.MaxValue)]
    public int DisplayOrder { get; init; }

    [Range(1, long.MaxValue)]
    public long ExpectedRevision { get; init; }
}

public sealed class ShippingMethodTransitionRequest
{
    [Range(1, long.MaxValue)]
    public long ExpectedRevision { get; init; }
}

public sealed class UpsertShippingProvinceRateRequest
{
    [Range(typeof(decimal), "0", "9999999999999999")]
    public decimal SurchargeFee { get; init; }

    public bool IsActive { get; init; } = true;

    [Range(1, long.MaxValue)]
    public long ExpectedRevision { get; init; }
}

public interface IShippingAdminService
{
    Task<IReadOnlyList<AdminShippingMethodSummaryDto>> GetMethodsAsync(CancellationToken ct = default);
    Task<AdminShippingMethodDetailDto> GetMethodAsync(Guid methodId, CancellationToken ct = default);
    Task<AdminShippingMethodDetailDto> CreateAsync(Guid adminId, CreateShippingMethodRequest request, string correlationId, CancellationToken ct = default);
    Task<AdminShippingMethodDetailDto> UpdateAsync(Guid adminId, Guid methodId, UpdateShippingMethodRequest request, string correlationId, CancellationToken ct = default);
    Task<AdminShippingMethodDetailDto> ActivateAsync(Guid adminId, Guid methodId, long expectedRevision, string correlationId, CancellationToken ct = default);
    Task<AdminShippingMethodDetailDto> DeactivateAsync(Guid adminId, Guid methodId, long expectedRevision, string correlationId, CancellationToken ct = default);
    Task<AdminShippingMethodDetailDto> MakeDefaultAsync(Guid adminId, Guid methodId, long expectedRevision, string correlationId, CancellationToken ct = default);
    Task<AdminShippingMethodDetailDto> UpsertProvinceRateAsync(Guid adminId, Guid methodId, string province, UpsertShippingProvinceRateRequest request, string correlationId, CancellationToken ct = default);
    Task<AdminShippingMethodDetailDto> RemoveProvinceRateAsync(Guid adminId, Guid methodId, string province, long expectedRevision, string correlationId, CancellationToken ct = default);
}

public sealed class ShippingFailure(string code, string message, int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
