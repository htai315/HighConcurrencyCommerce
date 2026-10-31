using System.Data;
using System.Text.Json;
using HighConcurrencyCommerce.Application.Inventory;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HighConcurrencyCommerce.Infrastructure.Inventory;

public sealed class PostgresInventoryService(StoreDbContext db) : IInventoryService
{
    private async Task<InventoryAdjustmentDto> GetAdjustmentDtoAsync(Guid requestId, CancellationToken ct)
    {
        var result = await db.InventoryAdjustmentRequests
            .AsNoTracking()
            .Where(r => r.Id == requestId)
            .Select(r => new InventoryAdjustmentDto(
                r.Id,
                r.ProductSkuId,
                r.ProductSku.SkuCode,
                r.ProductSku.Product.Title,
                r.RequestedBy,
                r.RequestedByNavigation.FullName,
                r.ReviewedBy,
                r.ReviewedByNavigation != null ? r.ReviewedByNavigation.FullName : null,
                r.DeltaQty,
                r.Reason,
                r.ReviewStatus,
                r.CreatedAt,
                r.ReviewedAt))
            .SingleOrDefaultAsync(ct);

        return result ?? throw new InventoryFailure("NOT_FOUND", "Không tìm thấy yêu cầu điều chỉnh tồn kho.");
    }

    public async Task<IReadOnlyList<SkuInventorySummaryDto>> GetInventorySummariesAsync(CancellationToken ct = default)
    {
        var summaries = await db.ProductSkus
            .Include(s => s.Product)
            .Include(s => s.InventoryBalance)
            .OrderByDescending(s => s.Product.CreatedAt)
            .ThenBy(s => s.SkuCode)
            .Select(s => new SkuInventorySummaryDto(
                s.Id,
                s.SkuCode,
                s.OptionLabel,
                s.ProductId,
                s.Product.Title,
                s.InventoryBalance != null ? s.InventoryBalance.PhysicalQty : 0,
                s.InventoryBalance != null ? s.InventoryBalance.AvailableQty : 0,
                s.InventoryBalance != null ? s.InventoryBalance.ReservedQty : 0,
                s.InventoryBalance != null ? s.InventoryBalance.AllocatedQty : 0,
                s.SkuStatus,
                s.Product.PublicationStatus))
            .ToListAsync(ct);

        return summaries;
    }

    public async Task<IReadOnlyList<InventoryMovementDto>> GetMovementsBySkuAsync(Guid skuId, CancellationToken ct = default)
    {
        var skuExists = await db.ProductSkus.AnyAsync(s => s.Id == skuId, ct);
        if (!skuExists)
        {
            throw new InventoryFailure("NOT_FOUND", "Không tìm thấy biến thể SKU.");
        }

        var movements = await db.InventoryMovements
            .Where(m => m.ProductSkuId == skuId)
            .Include(m => m.Actor)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new InventoryMovementDto(
                m.Id,
                m.ProductSkuId,
                m.MovementType,
                m.QtyDelta,
                m.SourceType,
                m.SourceId,
                m.ActorId,
                m.Actor != null ? m.Actor.FullName : null,
                m.Note,
                m.CreatedAt))
            .ToListAsync(ct);

        return movements;
    }

    public async Task<StockInResultDto> StockInAsync(Guid staffId, StockInRequest request, CancellationToken ct = default)
    {
        if (request.Quantity <= 0)
        {
            throw new InventoryFailure("INVALID_ARGUMENT", "Số lượng nhập kho phải lớn hơn 0.");
        }

        var sourceType = string.IsNullOrWhiteSpace(request.SourceType) ? "MANUAL_STOCK_IN" : request.SourceType.Trim();
        var sourceId = request.SourceId ?? Guid.NewGuid();

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // Khóa hàng ProductSku cha để bảo đảm tuần tự hóa ngay cả khi InventoryBalance chưa được tạo
        var sku = await db.ProductSkus
            .FromSqlInterpolated($"SELECT * FROM store.product_skus WHERE id = {request.ProductSkuId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);
        if (sku is null)
        {
            throw new InventoryFailure("NOT_FOUND", "Không tìm thấy biến thể SKU.");
        }

        // Idempotency check: Đã từng ghi nhận movement cho cùng source này chưa?
        var existingMovement = await db.InventoryMovements
            .FirstOrDefaultAsync(m => m.ProductSkuId == request.ProductSkuId
                && m.MovementType == "STOCK_IN"
                && m.SourceType == sourceType
                && m.SourceId == sourceId, ct);

        if (existingMovement is not null)
        {
            var requestedNote = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
            if (existingMovement.QtyDelta != request.Quantity || existingMovement.Note != requestedNote)
            {
                throw new InventoryFailure("IDEMPOTENCY_CONFLICT",
                    $"Yêu cầu nhập kho có cùng sourceId ({sourceId}) nhưng nội dung payload (số lượng hoặc ghi chú) khác với giao dịch đã ghi nhận.");
            }

            var currentBalance = await db.InventoryBalances.FirstOrDefaultAsync(b => b.ProductSkuId == request.ProductSkuId, ct);
            if (currentBalance is not null)
            {
                return new StockInResultDto(
                    currentBalance.Id,
                    currentBalance.ProductSkuId,
                    currentBalance.PhysicalQty,
                    currentBalance.AvailableQty,
                    currentBalance.ReservedQty,
                    currentBalance.AllocatedQty,
                    currentBalance.Revision,
                    existingMovement.Id);
            }
        }

        // Khóa hàng tồn kho theo SKU để tuần tự hóa cập nhật
        var balance = await db.InventoryBalances
            .FromSqlInterpolated($"SELECT * FROM store.inventory_balances WHERE product_sku_id = {request.ProductSkuId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);

        var now = DateTime.UtcNow;
        if (balance is null)
        {
            balance = new InventoryBalance
            {
                Id = Guid.NewGuid(),
                ProductSkuId = request.ProductSkuId,
                PhysicalQty = request.Quantity,
                AvailableQty = request.Quantity,
                ReservedQty = 0,
                AllocatedQty = 0,
                Revision = 1,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.InventoryBalances.Add(balance);
        }
        else
        {
            balance.PhysicalQty += request.Quantity;
            balance.AvailableQty += request.Quantity;
            balance.Revision += 1;
            balance.UpdatedAt = now;
        }

        var movement = new InventoryMovement
        {
            Id = Guid.NewGuid(),
            ProductSkuId = request.ProductSkuId,
            MovementType = "STOCK_IN",
            QtyDelta = request.Quantity,
            SourceType = sourceType,
            SourceId = sourceId,
            ActorId = staffId,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.InventoryMovements.Add(movement);

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InventoryFailure("CONFLICT", "Phiếu nhập kho này đã được xử lý (trùng SourceId và SourceType).");
        }

        return new StockInResultDto(
            balance.Id,
            balance.ProductSkuId,
            balance.PhysicalQty,
            balance.AvailableQty,
            balance.ReservedQty,
            balance.AllocatedQty,
            balance.Revision,
            movement.Id);
    }

    public async Task<InventoryAdjustmentDto> CreateAdjustmentRequestAsync(
        Guid staffId,
        CreateInventoryAdjustmentRequest request,
        CancellationToken ct = default)
    {
        if (request.ProductSkuId == Guid.Empty || request.Quantity <= 0)
        {
            throw new InventoryFailure("INVALID_ARGUMENT", "SKU và số lượng đề nghị giảm phải hợp lệ.");
        }

        var reason = request.Reason.Trim();
        if (reason.Length == 0)
        {
            throw new InventoryFailure("INVALID_ARGUMENT", "Lý do điều chỉnh không được để trống.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // Shared inventory serialization boundary: SKU parent is always locked before its balance.
        var sku = await db.ProductSkus
            .FromSqlInterpolated($"SELECT * FROM store.product_skus WHERE id = {request.ProductSkuId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);
        if (sku is null)
        {
            throw new InventoryFailure("NOT_FOUND", "Không tìm thấy biến thể SKU.");
        }

        var balance = await db.InventoryBalances
            .FirstOrDefaultAsync(b => b.ProductSkuId == request.ProductSkuId, ct);
        if (balance is null || balance.AvailableQty < request.Quantity)
        {
            throw new InventoryFailure("INSUFFICIENT_AVAILABLE", "Tồn khả dụng hiện tại không đủ cho số lượng đề nghị giảm.");
        }

        var now = DateTime.UtcNow;
        var adjustment = new InventoryAdjustmentRequest
        {
            Id = Guid.NewGuid(),
            ProductSkuId = request.ProductSkuId,
            RequestedBy = staffId,
            DeltaQty = -request.Quantity,
            Reason = reason,
            ReviewStatus = "PENDING",
            CreatedAt = now,
            UpdatedAt = now
        };

        db.InventoryAdjustmentRequests.Add(adjustment);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return await GetAdjustmentDtoAsync(adjustment.Id, ct);
    }

    public async Task<IReadOnlyList<InventoryAdjustmentDto>> GetAdjustmentRequestsAsync(CancellationToken ct = default) =>
        await db.InventoryAdjustmentRequests
            .AsNoTracking()
            .OrderBy(r => r.ReviewStatus == "PENDING" ? 0 : 1)
            .ThenByDescending(r => r.CreatedAt)
            .Select(r => new InventoryAdjustmentDto(
                r.Id,
                r.ProductSkuId,
                r.ProductSku.SkuCode,
                r.ProductSku.Product.Title,
                r.RequestedBy,
                r.RequestedByNavigation.FullName,
                r.ReviewedBy,
                r.ReviewedByNavigation != null ? r.ReviewedByNavigation.FullName : null,
                r.DeltaQty,
                r.Reason,
                r.ReviewStatus,
                r.CreatedAt,
                r.ReviewedAt))
            .ToListAsync(ct);

    public async Task<InventoryAdjustmentDecisionResultDto> DecideAdjustmentRequestAsync(
        Guid adminId,
        Guid requestId,
        InventoryAdjustmentDecisionRequest request,
        string correlationId,
        CancellationToken ct = default)
    {
        var decision = request.Decision.Trim().ToUpperInvariant();
        if (decision is not ("APPROVE" or "REJECT"))
        {
            throw new InventoryFailure("INVALID_ARGUMENT", "Quyết định chỉ nhận APPROVE hoặc REJECT.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var adjustment = await db.InventoryAdjustmentRequests
            .FromSqlInterpolated($"SELECT * FROM store.inventory_adjustment_requests WHERE id = {requestId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);
        if (adjustment is null)
        {
            throw new InventoryFailure("NOT_FOUND", "Không tìm thấy yêu cầu điều chỉnh tồn kho.");
        }

        if (adjustment.ReviewStatus != "PENDING")
        {
            throw new InventoryFailure("ALREADY_DECIDED", "Yêu cầu điều chỉnh tồn kho đã được xử lý.");
        }

        if (adjustment.RequestedBy == adminId)
        {
            throw new InventoryFailure("SELF_REVIEW", "Người tạo yêu cầu không được tự duyệt yêu cầu của chính mình.");
        }

        var now = DateTime.UtcNow;
        adjustment.ReviewedBy = adminId;
        adjustment.ReviewedAt = now;
        adjustment.UpdatedAt = now;

        InventoryBalanceSnapshotDto? balanceResult = null;
        Guid? movementId = null;

        if (decision == "REJECT")
        {
            adjustment.ReviewStatus = "REJECTED";
        }
        else
        {
            var sku = await db.ProductSkus
                .FromSqlInterpolated($"SELECT * FROM store.product_skus WHERE id = {adjustment.ProductSkuId} FOR UPDATE")
                .FirstOrDefaultAsync(ct);
            if (sku is null)
            {
                throw new InventoryFailure("NOT_FOUND", "Không tìm thấy biến thể SKU.");
            }

            var balance = await db.InventoryBalances
                .FromSqlInterpolated($"SELECT * FROM store.inventory_balances WHERE product_sku_id = {adjustment.ProductSkuId} FOR UPDATE")
                .FirstOrDefaultAsync(ct);
            var decrease = -adjustment.DeltaQty;
            if (balance is null || balance.AvailableQty < decrease)
            {
                throw new InventoryFailure("INSUFFICIENT_AVAILABLE", "Tồn khả dụng tại thời điểm duyệt không đủ để áp dụng điều chỉnh.");
            }

            balance.PhysicalQty -= decrease;
            balance.AvailableQty -= decrease;
            balance.Revision += 1;
            balance.UpdatedAt = now;

            var movement = new InventoryMovement
            {
                Id = Guid.NewGuid(),
                ProductSkuId = adjustment.ProductSkuId,
                MovementType = "ADJUST",
                QtyDelta = adjustment.DeltaQty,
                SourceType = "INVENTORY_ADJUSTMENT",
                SourceId = adjustment.Id,
                ActorId = adminId,
                Note = adjustment.Reason,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.InventoryMovements.Add(movement);
            movementId = movement.Id;
            adjustment.ReviewStatus = "APPLIED";
            balanceResult = new InventoryBalanceSnapshotDto(
                balance.Id,
                balance.ProductSkuId,
                balance.PhysicalQty,
                balance.AvailableQty,
                balance.ReservedQty,
                balance.AllocatedQty,
                balance.Revision);
        }

        var auditAction = adjustment.ReviewStatus == "APPLIED"
            ? "INVENTORY_ADJUSTMENT_APPLIED"
            : "INVENTORY_ADJUSTMENT_REJECTED";
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = adminId,
            ActorType = "USER",
            Action = auditAction,
            EntityType = "InventoryAdjustmentRequest",
            EntityId = adjustment.Id,
            BeforeData = JsonSerializer.Serialize(new { reviewStatus = "PENDING" }),
            AfterData = JsonSerializer.Serialize(new
            {
                reviewStatus = adjustment.ReviewStatus,
                adjustment.DeltaQty,
                adjustment.ReviewedAt
            }),
            CorrelationId = correlationId,
            OccurredAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var dto = await GetAdjustmentDtoAsync(adjustment.Id, ct);
        return new InventoryAdjustmentDecisionResultDto(dto, balanceResult, movementId);
    }
}
