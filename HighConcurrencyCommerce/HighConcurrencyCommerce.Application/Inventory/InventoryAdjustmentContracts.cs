using System.ComponentModel.DataAnnotations;

namespace HighConcurrencyCommerce.Application.Inventory;

public sealed class CreateInventoryAdjustmentRequest
{
    [Required(ErrorMessage = "Mã biến thể SKU không được để trống.")]
    public Guid ProductSkuId { get; set; }

    [Range(1, 1_000_000, ErrorMessage = "Số lượng đề nghị giảm phải từ 1 đến 1,000,000.")]
    public int Quantity { get; set; }

    [Required(ErrorMessage = "Lý do điều chỉnh không được để trống.")]
    [StringLength(500, MinimumLength = 1, ErrorMessage = "Lý do điều chỉnh tối đa 500 ký tự.")]
    public string Reason { get; set; } = string.Empty;
}

public sealed class InventoryAdjustmentDecisionRequest
{
    [Required(ErrorMessage = "Quyết định không được để trống.")]
    [RegularExpression("^(APPROVE|REJECT)$", ErrorMessage = "Quyết định chỉ nhận APPROVE hoặc REJECT.")]
    public string Decision { get; set; } = string.Empty;
}

public sealed record InventoryAdjustmentDto(
    Guid Id,
    Guid ProductSkuId,
    string SkuCode,
    string ProductTitle,
    Guid RequestedBy,
    string RequestedByName,
    Guid? ReviewedBy,
    string? ReviewedByName,
    int DeltaQty,
    string Reason,
    string ReviewStatus,
    DateTime CreatedAt,
    DateTime? ReviewedAt);

public sealed record InventoryAdjustmentDecisionResultDto(
    InventoryAdjustmentDto Request,
    InventoryBalanceSnapshotDto? Balance,
    Guid? MovementId);

public sealed record InventoryBalanceSnapshotDto(
    Guid BalanceId,
    Guid ProductSkuId,
    int PhysicalQty,
    int AvailableQty,
    int ReservedQty,
    int AllocatedQty,
    long Revision);
