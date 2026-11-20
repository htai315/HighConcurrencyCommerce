using System.ComponentModel.DataAnnotations;

namespace HighConcurrencyCommerce.Application.Vouchers;

public sealed class CreateVoucherRequest
{
    private string _code = string.Empty;
    private string _name = string.Empty;

    [Required(ErrorMessage = "Mã voucher không được để trống.")]
    [RegularExpression(@"^[A-Za-z0-9_-]{3,32}$", ErrorMessage = "Mã voucher từ 3 đến 32 ký tự, chỉ gồm chữ cái, số, gạch ngang và gạch dưới.")]
    public string Code
    {
        get => _code;
        set => _code = value?.Trim() ?? string.Empty;
    }

    [StringLength(100, ErrorMessage = "Tên chương trình voucher không được vượt quá 100 ký tự.")]
    public string Name
    {
        get => string.IsNullOrWhiteSpace(_name) ? Code : _name;
        set => _name = value?.Trim() ?? string.Empty;
    }

    [Required(ErrorMessage = "Loại chiết khấu không được để trống.")]
    public string DiscountType { get; set; } = string.Empty;

    public decimal DiscountValue { get; set; }

    public decimal? MaxDiscountAmount { get; set; }

    public decimal MinimumOrderAmount { get; set; } = 0m;

    public int TotalUsageLimit { get; set; }

    public int PerCustomerLimit { get; set; }

    public decimal BudgetTotal { get; set; }

    public DateTime StartsAt { get; set; }

    public DateTime EndsAt { get; set; }
}

public sealed class UpdateVoucherRequest
{
    private string _code = string.Empty;
    private string _name = string.Empty;

    [Required(ErrorMessage = "Mã voucher không được để trống.")]
    [RegularExpression(@"^[A-Za-z0-9_-]{3,32}$", ErrorMessage = "Mã voucher từ 3 đến 32 ký tự, chỉ gồm chữ cái, số, gạch ngang và gạch dưới.")]
    public string Code
    {
        get => _code;
        set => _code = value?.Trim() ?? string.Empty;
    }

    [StringLength(100, ErrorMessage = "Tên chương trình voucher không được vượt quá 100 ký tự.")]
    public string Name
    {
        get => string.IsNullOrWhiteSpace(_name) ? Code : _name;
        set => _name = value?.Trim() ?? string.Empty;
    }

    [Required(ErrorMessage = "Loại chiết khấu không được để trống.")]
    public string DiscountType { get; set; } = string.Empty;

    public decimal DiscountValue { get; set; }

    public decimal? MaxDiscountAmount { get; set; }

    public decimal MinimumOrderAmount { get; set; } = 0m;

    public int TotalUsageLimit { get; set; }

    public int PerCustomerLimit { get; set; }

    public decimal BudgetTotal { get; set; }

    public DateTime StartsAt { get; set; }

    public DateTime EndsAt { get; set; }
}

public sealed class AdminVoucherQuery
{
    [Range(1, 100_000, ErrorMessage = "Trang phải từ 1 đến 100.000.")]
    public int Page { get; set; } = 1;

    [Range(1, 100, ErrorMessage = "Số mục mỗi trang phải từ 1 đến 100.")]
    public int PageSize { get; set; } = 20;

    public string? Status { get; set; }
    public string? Q { get; set; }
}

public sealed record AdminVoucherSummaryDto(
    Guid Id,
    string Code,
    string Name,
    string VoucherStatus,
    string DiscountType,
    decimal DiscountValue,
    decimal? MaxDiscountAmount,
    decimal MinimumOrderAmount,
    int TotalUsageLimit,
    int PerCustomerLimit,
    int UsageHeld,
    int UsageConsumed,
    int RemainingUsage,
    decimal BudgetTotal,
    decimal BudgetHeld,
    decimal BudgetConsumed,
    decimal RemainingBudget,
    DateTime StartsAt,
    DateTime EndsAt,
    DateTime? ActivatedAt,
    Guid CreatedBy,
    Guid? ActivatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record AdminVoucherPageDto(
    IReadOnlyList<AdminVoucherSummaryDto> Items,
    int TotalCount,
    int Page,
    int PageSize);

public sealed record AdminVoucherDetailDto(
    Guid Id,
    string Code,
    string Name,
    string VoucherStatus,
    string DiscountType,
    decimal DiscountValue,
    decimal? MaxDiscountAmount,
    decimal MinimumOrderAmount,
    int TotalUsageLimit,
    int PerCustomerLimit,
    int UsageHeld,
    int UsageConsumed,
    int RemainingUsage,
    decimal BudgetTotal,
    decimal BudgetHeld,
    decimal BudgetConsumed,
    decimal RemainingBudget,
    DateTime StartsAt,
    DateTime EndsAt,
    DateTime? ActivatedAt,
    Guid CreatedBy,
    Guid? ActivatedBy,
    int Revision,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed class VoucherFailure(string code, string message = "", int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public interface IVoucherAdminService
{
    Task<AdminVoucherPageDto> GetVouchersAsync(AdminVoucherQuery query, CancellationToken ct = default);
    Task<AdminVoucherDetailDto> GetVoucherByIdAsync(Guid id, CancellationToken ct = default);
    Task<AdminVoucherDetailDto> CreateVoucherAsync(Guid adminId, CreateVoucherRequest request, string correlationId, CancellationToken ct = default);
    Task<AdminVoucherDetailDto> UpdateVoucherAsync(Guid adminId, Guid id, UpdateVoucherRequest request, string correlationId, CancellationToken ct = default);
    Task<AdminVoucherDetailDto> ActivateVoucherAsync(Guid adminId, Guid id, string correlationId, CancellationToken ct = default);
    Task<AdminVoucherDetailDto> PauseVoucherAsync(Guid adminId, Guid id, string correlationId, CancellationToken ct = default);
    Task<AdminVoucherDetailDto> ResumeVoucherAsync(Guid adminId, Guid id, string correlationId, CancellationToken ct = default);
    Task<AdminVoucherDetailDto> EndVoucherAsync(Guid adminId, Guid id, string correlationId, CancellationToken ct = default);
}

public sealed class PreviewVoucherRequest
{
    private string _code = string.Empty;

    [Required(ErrorMessage = "Mã voucher không được để trống.")]
    public string Code
    {
        get => _code;
        set => _code = value?.Trim() ?? string.Empty;
    }
}

public sealed record VoucherPreviewDto(
    string Code,
    decimal SubtotalAmount,
    decimal DiscountAmount,
    decimal TotalAmount,
    DateTime ValidUntil);

public interface IVoucherCustomerService
{
    Task<VoucherPreviewDto> PreviewVoucherAsync(Guid customerId, PreviewVoucherRequest request, CancellationToken ct = default);
}
