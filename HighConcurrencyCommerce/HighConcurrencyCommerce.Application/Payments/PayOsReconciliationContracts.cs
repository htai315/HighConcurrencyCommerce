using System.ComponentModel.DataAnnotations;

namespace HighConcurrencyCommerce.Application.Payments;

public sealed class PayOsReconciliationQuery
{
    [Range(1, 100_000)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 25;
    public string? Status { get; set; }
    public string? Reason { get; set; }
    [StringLength(120)] public string? Q { get; set; }
}

public sealed record PayOsReconciliationSummaryDto(
    Guid Id, Guid PaymentId, Guid OrderId, string OrderNumber, string CustomerEmail,
    string Reason, string CaseStatus, decimal ExpectedAmount, string PaymentStatus,
    Guid? AssignedTo, string? AssignedToName, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record PayOsReconciliationTransactionDto(
    Guid Id, string? ProviderTransactionId, decimal Amount, string TransactionStatus,
    DateTime? ProviderPaidAt, DateTime VerifiedAt);

public sealed record PayOsReconciliationDetailDto(
    Guid Id, Guid PaymentId, Guid OrderId, string OrderNumber, Guid CustomerId,
    string CustomerEmail, string CustomerName, string Reason, string CaseStatus,
    string PaymentStatus, decimal ExpectedAmount, Guid? AssignedTo, string? AssignedToName,
    DateTime? AssignedAt, Guid? ResolvedBy, string? ResolvedByName, DateTime? ResolvedAt,
    string? Note, DateTime CreatedAt, DateTime UpdatedAt,
    IReadOnlyList<PayOsReconciliationTransactionDto> Transactions,
    PayOsManualRefundDto? ManualRefund);

public sealed record PayOsManualRefundDto(
    Guid Id, Guid TransactionId, decimal Amount, string EvidenceReference,
    string RefundStatus, DateTime? RefundedAt, string AdminName);

public sealed record PayOsReconciliationPageDto(
    IReadOnlyList<PayOsReconciliationSummaryDto> Items, int Page, int PageSize, int TotalCount);

public sealed class ResolvePayOsReconciliationRequest
{
    [Required, StringLength(1000, MinimumLength = 5)] public string Note { get; set; } = string.Empty;
}

public sealed class ManualRefundPayOsReconciliationRequest
{
    [Required, StringLength(100, MinimumLength = 3)] public string EvidenceReference { get; set; } = string.Empty;
    [Required, StringLength(1000, MinimumLength = 5)] public string Note { get; set; } = string.Empty;
}

public interface IPayOsReconciliationService
{
    Task<PayOsReconciliationPageDto> GetAsync(PayOsReconciliationQuery query, CancellationToken ct = default);
    Task<PayOsReconciliationDetailDto> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<PayOsReconciliationDetailDto> ClaimAsync(Guid adminId, Guid id, string correlationId, CancellationToken ct = default);
    Task<PayOsReconciliationDetailDto> ResolveAsync(Guid adminId, Guid id, ResolvePayOsReconciliationRequest request, string correlationId, CancellationToken ct = default);
    Task<PayOsReconciliationDetailDto> RefundAsync(Guid adminId, Guid id, ManualRefundPayOsReconciliationRequest request, string correlationId, CancellationToken ct = default);
}

public sealed class PayOsReconciliationFailure(string code, string message, int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
