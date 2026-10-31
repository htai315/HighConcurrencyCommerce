using System.ComponentModel.DataAnnotations;

namespace HighConcurrencyCommerce.Application.Inventory;

public sealed record SkuInventorySummaryDto(
    Guid SkuId,
    string SkuCode,
    string OptionLabel,
    Guid ProductId,
    string ProductTitle,
    int PhysicalQty,
    int AvailableQty,
    int ReservedQty,
    int AllocatedQty,
    string SkuStatus,
    string PublicationStatus);

public sealed record InventoryMovementDto(
    Guid Id,
    Guid ProductSkuId,
    string MovementType,
    int QtyDelta,
    string SourceType,
    Guid SourceId,
    Guid? ActorId,
    string? ActorName,
    string? Note,
    DateTime CreatedAt);

public sealed class StockInRequest
{
    [Required(ErrorMessage = "Mã biến thể SKU không được để trống.")]
    public Guid ProductSkuId { get; set; }

    [Range(1, 1000000, ErrorMessage = "Số lượng nhập kho phải là số nguyên dương từ 1 đến 1,000,000.")]
    public int Quantity { get; set; }

    [StringLength(60, ErrorMessage = "Loại nguồn nhập kho tối đa 60 ký tự.")]
    public string? SourceType { get; set; }

    public Guid? SourceId { get; set; }

    [StringLength(500, ErrorMessage = "Ghi chú nhập kho tối đa 500 ký tự.")]
    public string? Note { get; set; }
}

public sealed record StockInResultDto(
    Guid BalanceId,
    Guid ProductSkuId,
    int PhysicalQty,
    int AvailableQty,
    int ReservedQty,
    int AllocatedQty,
    long Revision,
    Guid MovementId);

public sealed class InventoryFailure(string code, string message = "") : Exception(message)
{
    public string Code { get; } = code;
}

public interface IInventoryService
{
    Task<IReadOnlyList<SkuInventorySummaryDto>> GetInventorySummariesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<InventoryMovementDto>> GetMovementsBySkuAsync(Guid skuId, CancellationToken ct = default);
    Task<StockInResultDto> StockInAsync(Guid staffId, StockInRequest request, CancellationToken ct = default);
    Task<InventoryAdjustmentDto> CreateAdjustmentRequestAsync(Guid staffId, CreateInventoryAdjustmentRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<InventoryAdjustmentDto>> GetAdjustmentRequestsAsync(CancellationToken ct = default);
    Task<InventoryAdjustmentDecisionResultDto> DecideAdjustmentRequestAsync(
        Guid adminId,
        Guid requestId,
        InventoryAdjustmentDecisionRequest request,
        string correlationId,
        CancellationToken ct = default);
}

