using System.ComponentModel.DataAnnotations;

namespace HighConcurrencyCommerce.Application.CarrierTracking;

public static class CarrierTrackingStatuses
{
    public const string Draft = "DRAFT";
    public const string LabelReady = "LABEL_READY";
    public const string HandedOver = "HANDED_OVER";
    public const string InTransit = "IN_TRANSIT";
    public const string OutForDelivery = "OUT_FOR_DELIVERY";
    public const string DeliveryFailed = "DELIVERY_FAILED";
    public const string Delivered = "DELIVERED";
    public const string Returning = "RETURNING";
    public const string Returned = "RETURNED";
    public const string Cancelled = "CANCELLED";
}

public sealed record CarrierSummaryDto(
    Guid Id,
    string Code,
    string Name,
    string? Hotline,
    string? TrackingUrlTemplate,
    string CarrierStatus,
    long Revision,
    int ConsignmentCount,
    DateTime UpdatedAt);

public sealed record CarrierDetailDto(
    Guid Id,
    string Code,
    string Name,
    string? Hotline,
    string? TrackingUrlTemplate,
    string CarrierStatus,
    long Revision,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record CarrierLabelDto(string ContentType, long SizeBytes, string Sha256);

public sealed record CarrierTrackingEventDto(
    Guid Id,
    string EventKey,
    string EventType,
    string EventStatus,
    string? Location,
    string? Description,
    DateTime OccurredAt,
    DateTime RecordedAt,
    string SourceType);

public sealed record CarrierConsignmentDto(
    Guid Id,
    string TargetType,
    Guid TargetShipmentId,
    Guid CarrierId,
    string CarrierCode,
    string CarrierName,
    string TrackingNumber,
    string? TrackingUrl,
    string ConsignmentStatus,
    string? ServiceCode,
    string RecipientName,
    string RecipientPhone,
    string Destination,
    string? ShippingMethodCode,
    string? ShippingMethodName,
    CarrierLabelDto? Label,
    long Revision,
    DateTime? HandedOverAt,
    DateTime? DeliveredAt,
    DateTime? CancelledAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<CarrierTrackingEventDto> Events);

public sealed class CreateCarrierRequest
{
    [Required, StringLength(32, MinimumLength = 2)]
    [RegularExpression("^[A-Za-z0-9_-]{2,32}$")]
    public string Code { get; init; } = string.Empty;

    [Required, StringLength(120, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    [StringLength(32)]
    public string? Hotline { get; init; }

    [StringLength(500)]
    public string? TrackingUrlTemplate { get; init; }
}

public sealed class UpdateCarrierRequest
{
    [Required, StringLength(120, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    [StringLength(32)]
    public string? Hotline { get; init; }

    [StringLength(500)]
    public string? TrackingUrlTemplate { get; init; }

    [Range(1, long.MaxValue)]
    public long ExpectedRevision { get; init; }
}

public sealed class CarrierTransitionRequest
{
    [Range(1, long.MaxValue)]
    public long ExpectedRevision { get; init; }
}

public sealed class CreateCarrierConsignmentRequest
{
    [Required]
    public Guid CarrierId { get; init; }

    [Required, StringLength(100, MinimumLength = 3)]
    [RegularExpression("^[A-Za-z0-9._:/-]{3,100}$")]
    public string TrackingNumber { get; init; } = string.Empty;

    [StringLength(64)]
    public string? ServiceCode { get; init; }
}

public sealed class RecordCarrierTrackingEventRequest
{
    [Required, StringLength(180, MinimumLength = 3)]
    public string EventKey { get; init; } = string.Empty;

    [Required, StringLength(32)]
    [RegularExpression("^(PICKED_UP|IN_TRANSIT|OUT_FOR_DELIVERY|DELIVERY_FAILED|RETURNING)$")]
    public string EventType { get; init; } = string.Empty;

    [StringLength(200)]
    public string? Location { get; init; }

    [StringLength(500)]
    public string? Description { get; init; }

    public DateTime OccurredAt { get; init; }

    [Range(1, long.MaxValue)]
    public long ExpectedRevision { get; init; }
}

public sealed class CarrierConsignmentTransitionRequest
{
    [Range(1, long.MaxValue)]
    public long ExpectedRevision { get; init; }
}

public sealed record StoredCarrierLabel(string StorageKey, string ContentType, long Length, string Sha256);
public sealed record OpenCarrierLabel(Stream Content, string ContentType, long Length);

public interface ICarrierLabelStorage
{
    Task<StoredCarrierLabel> StoreAsync(Guid consignmentId, Stream content, string originalFileName, string declaredContentType, long length, CancellationToken ct = default);
    Task<OpenCarrierLabel?> OpenReadAsync(string storageKey, CancellationToken ct = default);
    Task DeleteAsync(string storageKey, CancellationToken ct = default);
}

public interface ICarrierAdminService
{
    Task<IReadOnlyList<CarrierSummaryDto>> GetCarriersAsync(string? query, string? status, CancellationToken ct = default);
    Task<CarrierDetailDto> GetCarrierAsync(Guid carrierId, CancellationToken ct = default);
    Task<CarrierDetailDto> CreateAsync(Guid adminId, CreateCarrierRequest request, string correlationId, CancellationToken ct = default);
    Task<CarrierDetailDto> UpdateAsync(Guid adminId, Guid carrierId, UpdateCarrierRequest request, string correlationId, CancellationToken ct = default);
    Task<CarrierDetailDto> ActivateAsync(Guid adminId, Guid carrierId, long expectedRevision, string correlationId, CancellationToken ct = default);
    Task<CarrierDetailDto> DeactivateAsync(Guid adminId, Guid carrierId, long expectedRevision, string correlationId, CancellationToken ct = default);
}

public interface ICarrierConsignmentService
{
    Task<IReadOnlyList<CarrierConsignmentDto>> GetQueueAsync(string? status, CancellationToken ct = default);
    Task<CarrierConsignmentDto> GetAsync(Guid consignmentId, CancellationToken ct = default);
    Task<CarrierConsignmentDto> CreateForSalesShipmentAsync(Guid staffId, Guid shipmentId, CreateCarrierConsignmentRequest request, string correlationId, CancellationToken ct = default);
    Task<CarrierConsignmentDto> CreateForExchangeShipmentAsync(Guid staffId, Guid shipmentId, CreateCarrierConsignmentRequest request, string correlationId, CancellationToken ct = default);
    Task<CarrierConsignmentDto> AttachLabelAsync(Guid staffId, Guid consignmentId, StoredCarrierLabel label, long expectedRevision, string correlationId, CancellationToken ct = default);
    Task<OpenCarrierLabel> OpenLabelAsync(Guid consignmentId, CancellationToken ct = default);
    Task<CarrierConsignmentDto> HandoverAsync(Guid staffId, Guid consignmentId, long expectedRevision, string correlationId, CancellationToken ct = default);
    Task<CarrierConsignmentDto> RecordEventAsync(Guid staffId, Guid consignmentId, RecordCarrierTrackingEventRequest request, string correlationId, CancellationToken ct = default);
    Task<CarrierConsignmentDto> CancelAsync(Guid staffId, Guid consignmentId, long expectedRevision, string correlationId, CancellationToken ct = default);
}

public interface ICustomerCarrierTrackingService
{
    Task<CarrierConsignmentDto> GetForOrderAsync(Guid customerId, Guid orderId, CancellationToken ct = default);
}

public sealed class CarrierTrackingFailure(string code, string message, int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
