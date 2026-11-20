using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace HighConcurrencyCommerce.Application.Returns;

public static class OrderReturnReasons
{
    public const string WrongItem = "WRONG_ITEM";
    public const string WrongSize = "WRONG_SIZE";
    public const string Defective = "DEFECTIVE";
    public const string NotAsDescribed = "NOT_AS_DESCRIBED";
    public const string Other = "OTHER";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        WrongItem, WrongSize, Defective, NotAsDescribed, Other
    };

    public static bool IsValid(string? reason) =>
        !string.IsNullOrWhiteSpace(reason) && All.Contains(reason.Trim());
}

public static class OrderReturnStatuses
{
    public const string Requested = "REQUESTED";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Cancelled = "CANCELLED";
    public const string Received = "RECEIVED";
    public const string Inspected = "INSPECTED";
    public const string Refunded = "REFUNDED";
    public const string ExchangeReady = "EXCHANGE_READY";
    public const string ExchangePacked = "EXCHANGE_PACKED";
    public const string ExchangeShipping = "EXCHANGE_SHIPPING";
    public const string Exchanged = "EXCHANGED";
    public const string ExchangeExpired = "EXCHANGE_EXPIRED";
    public const string ExchangeDeliveryFailed = "EXCHANGE_DELIVERY_FAILED";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Requested, Approved, Rejected, Cancelled, Received, Inspected, Refunded,
        ExchangeReady, ExchangePacked, ExchangeShipping, Exchanged, ExchangeExpired, ExchangeDeliveryFailed
    };

    public static readonly IReadOnlySet<string> Terminal = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Rejected, Cancelled, Refunded, Exchanged, ExchangeExpired, ExchangeDeliveryFailed
    };

    public static bool IsValid(string? status) =>
        !string.IsNullOrWhiteSpace(status) && All.Contains(status.Trim());

    public static bool IsTerminal(string? status) =>
        !string.IsNullOrWhiteSpace(status) && Terminal.Contains(status.Trim());
}

public static class OrderReturnResolutionTypes
{
    public const string Return = "RETURN";
    public const string Exchange = "EXCHANGE";
    public static bool IsValid(string? value) => string.Equals(value, Return, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, Exchange, StringComparison.OrdinalIgnoreCase);
}

public sealed class OrderReturnFailure(string code, string message = "", int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

// -----------------------------------------------------------------------------
// Calculator Contracts
// -----------------------------------------------------------------------------

public sealed record OrderItemRefundContext(
    Guid OrderItemId,
    Guid ProductSkuId,
    string ProductTitle,
    string SkuCode,
    string OptionLabel,
    int OrderedQuantity,
    decimal UnitPrice,
    decimal LineTotal);

public sealed record OrderRefundCalculationContext(
    Guid OrderId,
    decimal SubtotalAmount,
    decimal VoucherDiscountAmount,
    decimal TotalAmount,
    IReadOnlyList<OrderItemRefundContext> OrderItems,
    decimal ShippingFee = 0m);

public sealed record ReturnItemSelection(
    Guid OrderItemId,
    int RequestedQuantity);

public sealed record CalculatedReturnLine(
    Guid OrderItemId,
    Guid ProductSkuId,
    string ProductTitleSnapshot,
    string SkuCodeSnapshot,
    string OptionLabelSnapshot,
    int RequestedQuantity,
    decimal UnitPrice,
    decimal GrossAmount,
    decimal AllocatedVoucherDiscount,
    decimal RefundAmount);

public sealed record RefundCalculationResult(
    IReadOnlyList<CalculatedReturnLine> Lines,
    decimal TotalGrossAmount,
    decimal TotalAllocatedVoucherDiscount,
    decimal TotalRefundAmount);

public interface IRefundAllocationCalculator
{
    RefundCalculationResult Calculate(
        OrderRefundCalculationContext orderContext,
        IReadOnlyList<ReturnItemSelection> selections);
}

// -----------------------------------------------------------------------------
// Customer Contracts
// -----------------------------------------------------------------------------

public sealed record ExchangeTargetSkuDto(Guid SkuId, string SkuCode, string OptionLabel, decimal RegularPrice, int AvailableQty);

public sealed record EligibleOrderItemDto(
    Guid OrderItemId,
    Guid ProductSkuId,
    string ProductTitle,
    string SkuCode,
    string OptionLabel,
    int OrderedQuantity,
    decimal UnitPrice,
    decimal LineTotal,
    decimal MaxRefundableAmount,
    IReadOnlyList<ExchangeTargetSkuDto> ExchangeTargets);

public sealed record OrderReturnEligibilityDto(
    bool IsEligible,
    string? IneligibleReason,
    DateTime? DeliveredAt,
    DateTime? WindowExpiresAt,
    IReadOnlyList<EligibleOrderItemDto> Items,
    decimal ShippingFee = 0m,
    decimal OrderTotalAmount = 0m);

public sealed class CreateOrderReturnItemRequest
{
    public Guid OrderItemId { get; set; }

    [Range(1, 100_000, ErrorMessage = "Số lượng trả phải lớn hơn 0.")]
    public int RequestedQuantity { get; set; }

    public Guid? TargetSkuId { get; set; }
}

public sealed class CreateOrderReturnRequest
{
    private string _reasonCode = string.Empty;
    private string? _customerNote;
    private string _resolutionType = OrderReturnResolutionTypes.Return;

    public string ResolutionType
    {
        get => _resolutionType;
        set => _resolutionType = value?.Trim().ToUpperInvariant() ?? OrderReturnResolutionTypes.Return;
    }

    [Required(ErrorMessage = "Lý do trả hàng không được để trống.")]
    public string ReasonCode
    {
        get => _reasonCode;
        set => _reasonCode = value?.Trim().ToUpperInvariant() ?? string.Empty;
    }

    [StringLength(1000, ErrorMessage = "Ghi chú của khách hàng không được vượt quá 1000 ký tự.")]
    public string? CustomerNote
    {
        get => _customerNote;
        set => _customerNote = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    [Required(ErrorMessage = "Danh sách sản phẩm yêu cầu trả không được để trống.")]
    [MinLength(1, ErrorMessage = "Phải chọn ít nhất 1 sản phẩm để trả.")]
    public List<CreateOrderReturnItemRequest> Items { get; set; } = new();
}

public sealed class CustomerOrderReturnQuery
{
    [Range(1, 100_000, ErrorMessage = "Trang phải từ 1 đến 100.000.")]
    public int Page { get; set; } = 1;

    [Range(1, 100, ErrorMessage = "Số mục mỗi trang phải từ 1 đến 100.")]
    public int PageSize { get; set; } = 20;

    public string? Status { get; set; }
}

public sealed record OrderReturnLineDto(
    Guid Id,
    Guid OrderItemId,
    Guid ProductSkuId,
    string ProductTitle,
    string SkuCode,
    string OptionLabel,
    int RequestedQty,
    decimal UnitPrice,
    decimal GrossAmount,
    decimal AllocatedVoucherDiscount,
    decimal RefundAmount,
    int RestockQty,
    int QuarantineQty,
    int DamagedQty,
    int ScrappedQty,
    ExchangeLineDto? Exchange);

public sealed record ExchangeLineDto(
    Guid Id,
    Guid TargetSkuId,
    string TargetSkuCode,
    string TargetOptionLabel,
    decimal TargetRegularPrice,
    int Quantity,
    string? AllocationStatus,
    DateTime? AllocationExpiresAt);

public sealed record ExchangeShipmentDto(
    Guid Id,
    string ShipmentStatus,
    short RedeliveryCount,
    DateTime? ShippedAt,
    DateTime? DeliveredAt);

public sealed record CustomerOrderReturnSummaryDto(
    Guid Id,
    Guid OrderId,
    string OrderNumber,
    string ResolutionType,
    string ReturnStatus,
    string ReasonCode,
    decimal RefundAmount,
    int TotalItemCount,
    DateTime RequestedAt,
    DateTime UpdatedAt,
    string? EvidenceReference);

public sealed record CustomerOrderReturnPageDto(
    IReadOnlyList<CustomerOrderReturnSummaryDto> Items,
    int TotalCount,
    int Page,
    int PageSize);

public sealed record CustomerOrderReturnDetailDto(
    Guid Id,
    Guid OrderId,
    string OrderNumber,
    string ResolutionType,
    string ReturnStatus,
    string ReasonCode,
    string? CustomerNote,
    decimal RefundAmount,
    DateTime RequestedAt,
    DateTime? DecidedAt,
    string? DecisionNote,
    DateTime? ReceivedAt,
    DateTime? RefundedAt,
    string? EvidenceReference,
    IReadOnlyList<OrderReturnLineDto> Lines,
    ExchangeShipmentDto? ExchangeShipment,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    decimal ShippingFee = 0m,
    decimal OrderTotalAmount = 0m);

public interface IOrderReturnCustomerService
{
    Task<OrderReturnEligibilityDto> GetEligibilityAsync(Guid customerId, Guid orderId, CancellationToken ct = default);
    Task<CustomerOrderReturnDetailDto> CreateReturnRequestAsync(Guid customerId, Guid orderId, CreateOrderReturnRequest request, string correlationId, CancellationToken ct = default);
    Task<CustomerOrderReturnPageDto> GetCustomerReturnsAsync(Guid customerId, CustomerOrderReturnQuery query, CancellationToken ct = default);
    Task<CustomerOrderReturnDetailDto> GetCustomerReturnByIdAsync(Guid customerId, Guid returnId, CancellationToken ct = default);
    Task<CustomerOrderReturnDetailDto> CancelReturnRequestAsync(Guid customerId, Guid returnId, string correlationId, CancellationToken ct = default);
}

// -----------------------------------------------------------------------------
// Admin & Staff Contracts
// -----------------------------------------------------------------------------

public sealed class AdminOrderReturnQuery
{
    [Range(1, 100_000, ErrorMessage = "Trang phải từ 1 đến 100.000.")]
    public int Page { get; set; } = 1;

    [Range(1, 100, ErrorMessage = "Số mục mỗi trang phải từ 1 đến 100.")]
    public int PageSize { get; set; } = 20;

    public string? Status { get; set; }
    public string? Q { get; set; }
}

public sealed record AdminOrderReturnSummaryDto(
    Guid Id,
    Guid OrderId,
    string OrderNumber,
    Guid CustomerId,
    string CustomerEmail,
    string CustomerFullName,
    string ResolutionType,
    string ReturnStatus,
    string ReasonCode,
    decimal RefundAmount,
    int TotalItemCount,
    DateTime RequestedAt,
    DateTime? DecidedAt,
    DateTime? ReceivedAt,
    DateTime? RefundedAt,
    DateTime UpdatedAt);

public sealed record AdminOrderReturnPageDto(
    IReadOnlyList<AdminOrderReturnSummaryDto> Items,
    int TotalCount,
    int Page,
    int PageSize);

public sealed record AdminOrderReturnDetailDto(
    Guid Id,
    Guid OrderId,
    string OrderNumber,
    Guid CustomerId,
    string CustomerEmail,
    string CustomerFullName,
    string ResolutionType,
    string ReturnStatus,
    string ReasonCode,
    string? CustomerNote,
    decimal RefundAmount,
    DateTime RequestedAt,
    Guid? DecidedBy,
    string? DecidedByEmail,
    DateTime? DecidedAt,
    string? DecisionNote,
    Guid? ReceivedBy,
    string? ReceivedByEmail,
    DateTime? ReceivedAt,
    string? ReceivedEvidence,
    Guid? InspectedBy,
    string? InspectedByEmail,
    DateTime? InspectedAt,
    string? InspectionNote,
    DateTime? RefundedAt,
    string? EvidenceReference,
    IReadOnlyList<OrderReturnLineDto> Lines,
    ExchangeShipmentDto? ExchangeShipment,
    int Revision,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    decimal ShippingFee = 0m,
    decimal OrderTotalAmount = 0m);

public sealed class ApproveOrderReturnRequest
{
    [StringLength(1000, ErrorMessage = "Ghi chú phê duyệt không được vượt quá 1000 ký tự.")]
    public string? DecisionNote { get; set; }
}

public sealed class RejectOrderReturnRequest
{
    private string _decisionNote = string.Empty;

    [Required(ErrorMessage = "Lý do từ chối không được để trống.")]
    [StringLength(1000, MinimumLength = 3, ErrorMessage = "Lý do từ chối từ 3 đến 1000 ký tự.")]
    public string DecisionNote
    {
        get => _decisionNote;
        set => _decisionNote = value?.Trim() ?? string.Empty;
    }
}

public sealed class ProcessRefundRequest
{
    private string _evidenceReference = string.Empty;

    [Required(ErrorMessage = "Mã chứng từ hoàn tiền không được để trống.")]
    [StringLength(100, MinimumLength = 3, ErrorMessage = "Mã chứng từ hoàn tiền từ 3 đến 100 ký tự.")]
    public string EvidenceReference
    {
        get => _evidenceReference;
        set => _evidenceReference = value?.Trim() ?? string.Empty;
    }
}

public interface IOrderReturnAdminService
{
    Task<AdminOrderReturnPageDto> GetAdminReturnsAsync(AdminOrderReturnQuery query, CancellationToken ct = default);
    Task<AdminOrderReturnDetailDto> GetAdminReturnByIdAsync(Guid returnId, CancellationToken ct = default);
    Task<AdminOrderReturnDetailDto> ApproveReturnAsync(Guid adminId, Guid returnId, ApproveOrderReturnRequest request, string correlationId, CancellationToken ct = default);
    Task<AdminOrderReturnDetailDto> RejectReturnAsync(Guid adminId, Guid returnId, RejectOrderReturnRequest request, string correlationId, CancellationToken ct = default);
    Task<AdminOrderReturnDetailDto> ProcessRefundAsync(Guid adminId, Guid returnId, ProcessRefundRequest request, string correlationId, CancellationToken ct = default);
}

public sealed class StaffOrderReturnQuery
{
    [Range(1, 100_000, ErrorMessage = "Trang phải từ 1 đến 100.000.")]
    public int Page { get; set; } = 1;

    [Range(1, 100, ErrorMessage = "Số mục mỗi trang phải từ 1 đến 100.")]
    public int PageSize { get; set; } = 20;

    public string? Q { get; set; }
}

public sealed class StaffReceiveReturnRequest
{
    private string _receivedEvidence = string.Empty;

    [Required(ErrorMessage = "Ghi chú chứng từ nhận hàng không được để trống.")]
    [StringLength(500, MinimumLength = 3, ErrorMessage = "Chứng từ nhận hàng từ 3 đến 500 ký tự.")]
    public string ReceivedEvidence
    {
        get => _receivedEvidence;
        set => _receivedEvidence = value?.Trim() ?? string.Empty;
    }
}

public sealed class StaffInspectReturnLineRequest
{
    public Guid ReturnLineId { get; set; }

    [Range(0, 100_000)] public int RestockQty { get; set; }
    [Range(0, 100_000)] public int QuarantineQty { get; set; }
    [Range(0, 100_000)] public int DamagedQty { get; set; }
    [Range(0, 100_000)] public int ScrappedQty { get; set; }
}

public sealed class StaffInspectReturnRequest
{
    [Required, MinLength(1)]
    public List<StaffInspectReturnLineRequest> Lines { get; set; } = new();

    [StringLength(500)]
    public string? InspectionNote { get; set; }
}

public sealed record StaffOrderReturnSummaryDto(
    Guid Id,
    Guid OrderId,
    string OrderNumber,
    string ResolutionType,
    string ReturnStatus,
    string ReasonCode,
    decimal RefundAmount,
    int TotalItemCount,
    DateTime RequestedAt,
    DateTime? DecidedAt,
    string? DecisionNote);

public sealed record StaffOrderReturnPageDto(
    IReadOnlyList<StaffOrderReturnSummaryDto> Items,
    int TotalCount,
    int Page,
    int PageSize);

public interface IOrderReturnStaffService
{
    Task<StaffOrderReturnPageDto> GetApprovedReturnsQueueAsync(StaffOrderReturnQuery query, CancellationToken ct = default);
    Task<AdminOrderReturnDetailDto> GetReturnByIdAsync(Guid returnId, CancellationToken ct = default);
    Task<AdminOrderReturnDetailDto> ReceiveReturnAsync(Guid staffId, Guid returnId, StaffReceiveReturnRequest request, string correlationId, CancellationToken ct = default);
    Task<AdminOrderReturnDetailDto> InspectReturnAsync(Guid staffId, Guid returnId, StaffInspectReturnRequest request, string correlationId, CancellationToken ct = default);
}

public sealed record ExchangeFulfillmentLineDto(Guid TargetSkuId, string SkuCode, string OptionLabel, int Quantity);

public sealed record ExchangeFulfillmentDto(
    Guid ReturnRequestId,
    Guid ShipmentId,
    Guid OrderId,
    string OrderNumber,
    string ReturnStatus,
    string ShipmentStatus,
    short RedeliveryCount,
    DateTime? ShippedAt,
    DateTime? DeliveredAt,
    IReadOnlyList<ExchangeFulfillmentLineDto> Lines);

public sealed class ExchangeDeliveryResultRequest
{
    [Required, RegularExpression("^(DELIVERED|FAILED)$")]
    public string Result { get; set; } = "DELIVERED";

    [StringLength(500)]
    public string? Note { get; set; }
}

public interface IExchangeFulfillmentService
{
    Task<IReadOnlyList<ExchangeFulfillmentDto>> GetQueueAsync(CancellationToken ct = default);
    Task<ExchangeFulfillmentDto> PackAsync(Guid staffId, Guid shipmentId, string correlationId, CancellationToken ct = default);
    Task<ExchangeFulfillmentDto> HandoverAsync(Guid staffId, Guid shipmentId, string correlationId, CancellationToken ct = default);
    Task<ExchangeFulfillmentDto> HandoverWithConsignmentAsync(Guid staffId, Guid shipmentId, Guid consignmentId, long expectedRevision, string correlationId, CancellationToken ct = default);
    Task<ExchangeFulfillmentDto> RecordDeliveryAsync(Guid staffId, Guid shipmentId, ExchangeDeliveryResultRequest request, string correlationId, CancellationToken ct = default);
    Task<ExchangeFulfillmentDto> RetryAsync(Guid staffId, Guid shipmentId, string correlationId, CancellationToken ct = default);
    Task<ExchangeFulfillmentDto> ReturnStockAsync(Guid staffId, Guid shipmentId, string evidence, string correlationId, CancellationToken ct = default);
}

