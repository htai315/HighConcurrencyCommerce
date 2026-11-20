using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HighConcurrencyCommerce.Application.CarrierTracking;
using HighConcurrencyCommerce.Application.Notifications;
using HighConcurrencyCommerce.Application.Returns;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace HighConcurrencyCommerce.Infrastructure.Returns;

public sealed class PostgresExchangeFulfillmentService(
    StoreDbContext db,
    INotificationService notifications,
    TimeProvider timeProvider) : IExchangeFulfillmentService
{
    public async Task<IReadOnlyList<ExchangeFulfillmentDto>> GetQueueAsync(CancellationToken ct = default)
    {
        var shipments = await BaseQuery().AsNoTracking()
            .Where(s => s.ShipmentStatus != "DELIVERED" && !s.ReturnStockCompleted)
            .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id)
            .ToListAsync(ct);
        return shipments.Select(Map).ToList();
    }

    public async Task<ExchangeFulfillmentDto> PackAsync(Guid staffId, Guid shipmentId, string correlationId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var shipment = await LockAsync(shipmentId, ct);
        if (shipment.ShipmentStatus == "PACKED") { await tx.CommitAsync(ct); return Map(shipment); }
        EnsureState(shipment, "PENDING_PACKING");
        var now = timeProvider.GetUtcNow().UtcDateTime;
        shipment.ShipmentStatus = "PACKED";
        shipment.UpdatedAt = now;
        shipment.ReturnRequest.ReturnStatus = OrderReturnStatuses.ExchangePacked;
        Touch(shipment.ReturnRequest, now);
        Audit(staffId, shipment, "EXCHANGE_PACKED", correlationId, now);
        await SaveNotifyAsync(shipment, "EXCHANGE_PACKED", "Sản phẩm thay thế đã được đóng gói",
            $"Sản phẩm thay thế cho đơn {shipment.ReturnRequest.Order.OrderNumber} đã được đóng gói.", ct);
        await tx.CommitAsync(ct);
        return Map(shipment);
    }

    public async Task<ExchangeFulfillmentDto> HandoverAsync(Guid staffId, Guid shipmentId, string correlationId, CancellationToken ct = default)
        => await HandoverCoreAsync(staffId, shipmentId, null, null, correlationId, ct);

    public async Task<ExchangeFulfillmentDto> HandoverWithConsignmentAsync(
        Guid staffId, Guid shipmentId, Guid consignmentId, long expectedRevision,
        string correlationId, CancellationToken ct = default)
        => await HandoverCoreAsync(staffId, shipmentId, consignmentId, expectedRevision, correlationId, ct);

    private async Task<ExchangeFulfillmentDto> HandoverCoreAsync(
        Guid staffId, Guid shipmentId, Guid? requiredConsignmentId, long? expectedRevision,
        string correlationId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var shipment = await LockAsync(shipmentId, ct);
        var consignment = await LockConsignmentAsync(shipmentId, ct);
        ValidateRequiredConsignmentIdentity(consignment, requiredConsignmentId);
        if (shipment.ShipmentStatus == "SHIPPING" &&
            (consignment is null || consignment.ConsignmentStatus == "HANDED_OVER"))
        {
            await tx.CommitAsync(ct);
            return Map(shipment);
        }
        EnsureConsignmentRevision(consignment, expectedRevision);
        EnsureState(shipment, "PACKED");
        if (consignment is not null && consignment.ConsignmentStatus != "LABEL_READY")
            throw new CarrierTrackingFailure("TRACKING_INVALID_TRANSITION",
                "Vận đơn phải có nhãn LABEL_READY trước khi bàn giao.");
        var allocations = shipment.ReturnRequest.ExchangeAllocations.OrderBy(a => a.ProductSkuId).ToList();
        var skuIds = allocations.Select(a => a.ProductSkuId).Distinct().ToArray();
        await LockInventoryAsync(skuIds, ct);
        var balances = await db.InventoryBalances.Where(b => skuIds.Contains(b.ProductSkuId)).ToDictionaryAsync(b => b.ProductSkuId, ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var allocation in allocations)
        {
            if (allocation.AllocationStatus != "ALLOCATED")
                throw new OrderReturnFailure("EXCHANGE_INVALID_STATE", "Tồn thay thế chưa ở trạng thái được phân bổ.", 409);
            var balance = balances[allocation.ProductSkuId];
            if (balance.AllocatedQty < allocation.Quantity || balance.PhysicalQty < allocation.Quantity)
                throw new OrderReturnFailure("INVENTORY_INVARIANT", "Tồn phân bổ không đủ để bàn giao sản phẩm thay thế.", 409);
            balance.AllocatedQty -= allocation.Quantity;
            balance.PhysicalQty -= allocation.Quantity;
            balance.Revision += 1;
            balance.UpdatedAt = now;
            allocation.AllocationStatus = "CONSUMED";
            allocation.Revision += 1;
            allocation.UpdatedAt = now;
            AddMovement(allocation.ProductSkuId, "EXCHANGE_STOCK_OUT", -allocation.Quantity, staffId, shipment.ReturnRequestId, null, now);
        }
        shipment.ShipmentStatus = "SHIPPING";
        shipment.ShippedAt = now;
        shipment.UpdatedAt = now;
        shipment.ReturnRequest.ReturnStatus = OrderReturnStatuses.ExchangeShipping;
        Touch(shipment.ReturnRequest, now);
        if (consignment is not null)
            TransitionConsignment(consignment, staffId, "HANDED_OVER", "handover", "HANDED_OVER",
                "Kiện đổi đã được bàn giao cho đơn vị vận chuyển.", now, correlationId);
        var firstAttempt = new ExchangeDeliveryAttempt
        {
            Id = Guid.NewGuid(), ExchangeShipmentId = shipment.Id, AttemptNo = 1, Result = "IN_PROGRESS", CreatedAt = now, UpdatedAt = now
        };
        shipment.DeliveryAttempts.Add(firstAttempt);
        db.Entry(firstAttempt).State = EntityState.Added;
        Audit(staffId, shipment, "EXCHANGE_HANDOVER", correlationId, now);
        await SaveNotifyAsync(shipment, "EXCHANGE_SHIPPING", "Sản phẩm thay thế đang được giao",
            $"Kiện đổi của đơn {shipment.ReturnRequest.Order.OrderNumber} đã được bàn giao cho đơn vị vận chuyển.", ct);
        await tx.CommitAsync(ct);
        return Map(shipment);
    }

    public async Task<ExchangeFulfillmentDto> RecordDeliveryAsync(Guid staffId, Guid shipmentId, ExchangeDeliveryResultRequest request, string correlationId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = request.Result?.Trim().ToUpperInvariant();
        if (result is not ("DELIVERED" or "FAILED")) throw new OrderReturnFailure("INVALID_DELIVERY_RESULT", "Kết quả giao hàng không hợp lệ.", 400);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var shipment = await LockAsync(shipmentId, ct);
        var consignment = await LockConsignmentAsync(shipmentId, ct);
        if (shipment.ShipmentStatus == "DELIVERED" && result == "DELIVERED") { await tx.CommitAsync(ct); return Map(shipment); }
        EnsureState(shipment, "SHIPPING");
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var attempt = shipment.DeliveryAttempts.OrderByDescending(a => a.AttemptNo).FirstOrDefault(a => a.Result == "IN_PROGRESS")
            ?? throw new OrderReturnFailure("EXCHANGE_INVALID_STATE", "Không có lượt giao đang thực hiện.", 409);
        attempt.Result = result;
        attempt.Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        attempt.FinishedAt = now;
        attempt.UpdatedAt = now;
        shipment.ShipmentStatus = result == "DELIVERED" ? "DELIVERED" : "DELIVERY_FAILED";
        shipment.DeliveredAt = result == "DELIVERED" ? now : null;
        shipment.UpdatedAt = now;
        shipment.ReturnRequest.ReturnStatus = result == "DELIVERED" ? OrderReturnStatuses.Exchanged : OrderReturnStatuses.ExchangeDeliveryFailed;
        Touch(shipment.ReturnRequest, now);
        if (consignment is not null)
            TransitionConsignment(consignment, staffId,
                result == "DELIVERED" ? "DELIVERED" : "DELIVERY_FAILED",
                result == "DELIVERED" ? "delivered" : $"delivery-failed:{attempt.AttemptNo}",
                result, result == "DELIVERED" ? "Kiện đổi đã được giao thành công." : "Giao kiện đổi chưa thành công.",
                now, correlationId);
        Audit(staffId, shipment, result == "DELIVERED" ? "EXCHANGE_DELIVERED" : "EXCHANGE_DELIVERY_FAILED", correlationId, now);
        await SaveNotifyAsync(shipment, result == "DELIVERED" ? "EXCHANGE_DELIVERED" : "EXCHANGE_DELIVERY_FAILED",
            result == "DELIVERED" ? "Đổi hàng thành công" : "Giao sản phẩm thay thế chưa thành công",
            result == "DELIVERED"
                ? $"Sản phẩm thay thế cho đơn {shipment.ReturnRequest.Order.OrderNumber} đã được giao thành công."
                : $"Lượt giao sản phẩm thay thế cho đơn {shipment.ReturnRequest.Order.OrderNumber} chưa thành công.", ct);
        await tx.CommitAsync(ct);
        return Map(shipment);
    }

    public async Task<ExchangeFulfillmentDto> RetryAsync(Guid staffId, Guid shipmentId, string correlationId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var shipment = await LockAsync(shipmentId, ct);
        var consignment = await LockConsignmentAsync(shipmentId, ct);
        EnsureState(shipment, "DELIVERY_FAILED");
        if (shipment.RedeliveryCount >= 2) throw new OrderReturnFailure("REDELIVERY_LIMIT_REACHED", "Đã đạt giới hạn hai lần giao lại.", 409);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        shipment.RedeliveryCount += 1;
        shipment.ShipmentStatus = "SHIPPING";
        shipment.UpdatedAt = now;
        shipment.ReturnRequest.ReturnStatus = OrderReturnStatuses.ExchangeShipping;
        Touch(shipment.ReturnRequest, now);
        var attempt = new ExchangeDeliveryAttempt
        {
            Id = Guid.NewGuid(), ExchangeShipmentId = shipment.Id, AttemptNo = (short)(shipment.RedeliveryCount + 1),
            Result = "IN_PROGRESS", CreatedAt = now, UpdatedAt = now
        };
        shipment.DeliveryAttempts.Add(attempt);
        db.Entry(attempt).State = EntityState.Added;
        if (consignment is not null)
            TransitionConsignment(consignment, staffId, "OUT_FOR_DELIVERY",
                $"redelivery-started:{shipment.RedeliveryCount}", "REDELIVERY_STARTED",
                $"Bắt đầu giao lại kiện đổi lần {shipment.RedeliveryCount}.", now, correlationId);
        Audit(staffId, shipment, "EXCHANGE_REDELIVERY", correlationId, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Map(shipment);
    }

    public async Task<ExchangeFulfillmentDto> ReturnStockAsync(Guid staffId, Guid shipmentId, string evidence, string correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(evidence)) throw new OrderReturnFailure("RECEIVED_EVIDENCE_REQUIRED", "Chứng từ nhận lại kiện hàng là bắt buộc.", 400);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var shipment = await LockAsync(shipmentId, ct);
        var consignment = await LockConsignmentAsync(shipmentId, ct);
        if (shipment.ReturnStockCompleted) { await tx.CommitAsync(ct); return Map(shipment); }
        EnsureState(shipment, "DELIVERY_FAILED");
        var allocations = shipment.ReturnRequest.ExchangeAllocations.OrderBy(a => a.ProductSkuId).ToList();
        var skuIds = allocations.Select(a => a.ProductSkuId).Distinct().ToArray();
        await LockInventoryAsync(skuIds, ct);
        var balances = await db.InventoryBalances.Where(b => skuIds.Contains(b.ProductSkuId)).ToDictionaryAsync(b => b.ProductSkuId, ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var allocation in allocations)
        {
            var balance = balances[allocation.ProductSkuId];
            balance.PhysicalQty += allocation.Quantity;
            balance.QuarantinedQty += allocation.Quantity;
            balance.Revision += 1;
            balance.UpdatedAt = now;
            AddMovement(allocation.ProductSkuId, "EXCHANGE_RETURN_IN", allocation.Quantity, staffId, shipment.ReturnRequestId, evidence.Trim(), now);
        }
        shipment.ReturnStockCompleted = true;
        shipment.UpdatedAt = now;
        if (consignment is not null)
            TransitionConsignment(consignment, staffId, "RETURNED", "returned", "RETURNED",
                "Kiện đổi giao thất bại đã được nhận lại.", now, correlationId);
        Audit(staffId, shipment, "EXCHANGE_RETURN_STOCK", correlationId, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Map(shipment);
    }

    private IQueryable<ExchangeShipment> BaseQuery() => db.ExchangeShipments
        .Include(s => s.ReturnRequest).ThenInclude(r => r.Order)
        .Include(s => s.ReturnRequest).ThenInclude(r => r.ExchangeLines)
        .Include(s => s.ReturnRequest).ThenInclude(r => r.ExchangeAllocations)
        .Include(s => s.DeliveryAttempts);

    private async Task<ExchangeShipment> LockAsync(Guid shipmentId, CancellationToken ct)
    {
        var seed = await db.ExchangeShipments.AsNoTracking().FirstOrDefaultAsync(s => s.Id == shipmentId, ct)
            ?? throw new OrderReturnFailure("EXCHANGE_SHIPMENT_NOT_FOUND", "Không tìm thấy kiện giao sản phẩm thay thế.", 404);
        var returnSeed = await db.OrderReturnRequests.AsNoTracking().FirstAsync(r => r.Id == seed.ReturnRequestId, ct);
        await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM store.sales_orders WHERE id = {returnSeed.OrderId} FOR UPDATE").ToListAsync(ct);
        await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM store.order_return_requests WHERE id = {seed.ReturnRequestId} FOR UPDATE").ToListAsync(ct);
        await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM store.exchange_stock_allocations WHERE return_request_id = {seed.ReturnRequestId} ORDER BY product_sku_id FOR UPDATE").ToListAsync(ct);
        await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM store.exchange_shipments WHERE id = {shipmentId} FOR UPDATE").ToListAsync(ct);
        return await BaseQuery().FirstAsync(s => s.Id == shipmentId, ct);
    }

    private async Task<CarrierConsignment?> LockConsignmentAsync(Guid shipmentId, CancellationToken ct) =>
        await db.CarrierConsignments.FromSqlInterpolated(
            $"SELECT * FROM store.carrier_consignments WHERE exchange_shipment_id = {shipmentId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);

    private static void ValidateRequiredConsignmentIdentity(
        CarrierConsignment? consignment, Guid? requiredConsignmentId)
    {
        if (requiredConsignmentId is null) return;
        if (consignment is null || consignment.Id != requiredConsignmentId)
            throw new CarrierTrackingFailure("TRACKING_NOT_FOUND", "Không tìm thấy vận đơn cho kiện đổi.", 404);
    }

    private static void EnsureConsignmentRevision(CarrierConsignment? consignment, long? expectedRevision)
    {
        if (expectedRevision is null) return;
        if (consignment is null || consignment.Revision != expectedRevision)
            throw new CarrierTrackingFailure("CONSIGNMENT_STALE",
                $"Vận đơn đã thay đổi (revision hiện tại: {consignment?.Revision}). Hãy tải lại dữ liệu.", 409);
    }

    private void TransitionConsignment(
        CarrierConsignment consignment, Guid staffId, string status, string eventKey,
        string eventType, string description, DateTime now, string correlationId)
    {
        var before = JsonSerializer.Serialize(new { consignment.ConsignmentStatus, consignment.Revision });
        consignment.ConsignmentStatus = status;
        consignment.HandedOverAt ??= status == "HANDED_OVER" ? now : null;
        consignment.DeliveredAt = status == "DELIVERED" ? now : null;
        consignment.Revision++;
        consignment.UpdatedBy = staffId;
        consignment.UpdatedAt = now;
        var payload = JsonSerializer.Serialize(new
        {
            eventType, eventStatus = status, location = (string?)null, description,
            occurredAt = now.ToUniversalTime().ToString("O")
        });
        db.CarrierTrackingEvents.Add(new CarrierTrackingEvent
        {
            Id = Guid.NewGuid(), ConsignmentId = consignment.Id, EventKey = eventKey,
            EventType = eventType, EventStatus = status, Description = description,
            OccurredAt = now, RecordedAt = now, SourceType = "SYSTEM", ActorId = null,
            PayloadHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
        });
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(), ActorId = staffId, ActorType = "USER", Action = $"CARRIER_{eventType}",
            EntityType = "CarrierConsignment", EntityId = consignment.Id, BeforeData = before,
            AfterData = JsonSerializer.Serialize(new { consignment.ConsignmentStatus, consignment.Revision }),
            CorrelationId = correlationId, OccurredAt = now, CreatedAt = now, UpdatedAt = now
        });
    }

    private async Task LockInventoryAsync(Guid[] skuIds, CancellationToken ct)
    {
        await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM store.product_skus WHERE id = ANY({skuIds}) ORDER BY id FOR UPDATE").ToListAsync(ct);
        await db.Database.SqlQuery<Guid>($"SELECT product_sku_id AS \"Value\" FROM store.inventory_balances WHERE product_sku_id = ANY({skuIds}) ORDER BY product_sku_id FOR UPDATE").ToListAsync(ct);
    }

    private static void EnsureState(ExchangeShipment shipment, string expected)
    {
        if (shipment.ShipmentStatus != expected)
            throw new OrderReturnFailure("EXCHANGE_INVALID_STATE", $"Kiện đổi phải ở trạng thái {expected} (hiện tại: {shipment.ShipmentStatus}).", 409);
    }

    private static void Touch(OrderReturnRequest ret, DateTime now) { ret.UpdatedAt = now; ret.Revision += 1; }

    private void Audit(Guid actorId, ExchangeShipment shipment, string action, string correlationId, DateTime now) => db.AuditLogs.Add(new AuditLog
    {
        Id = Guid.NewGuid(), ActorId = actorId, ActorType = "USER", Action = action, EntityType = "ExchangeShipment",
        EntityId = shipment.Id, AfterData = JsonSerializer.Serialize(new { shipment.ReturnRequestId, shipment.ShipmentStatus }),
        CorrelationId = correlationId, CreatedAt = now, UpdatedAt = now, OccurredAt = now
    });

    private void AddMovement(Guid skuId, string type, int qty, Guid actorId, Guid returnId, string? note, DateTime now) => db.InventoryMovements.Add(new InventoryMovement
    {
        Id = Guid.NewGuid(), ProductSkuId = skuId, MovementType = type, QtyDelta = qty, SourceType = "ORDER_EXCHANGE",
        SourceId = returnId, ActorId = actorId, Note = note, CreatedAt = now, UpdatedAt = now
    });

    private async Task SaveNotifyAsync(ExchangeShipment shipment, string type, string title, string body, CancellationToken ct)
    {
        await notifications.EnsureAsync(shipment.ReturnRequest.CustomerId,
            $"order:{shipment.ReturnRequest.OrderId:N}:exchange:{shipment.ReturnRequestId:N}:{type.ToLowerInvariant()}", type, title, body, ct);
        await db.SaveChangesAsync(ct);
    }

    private static ExchangeFulfillmentDto Map(ExchangeShipment shipment) => new(
        shipment.ReturnRequestId, shipment.Id, shipment.ReturnRequest.OrderId, shipment.ReturnRequest.Order.OrderNumber,
        shipment.ReturnRequest.ReturnStatus, shipment.ShipmentStatus, shipment.RedeliveryCount, shipment.ShippedAt, shipment.DeliveredAt,
        shipment.ReturnRequest.ExchangeLines.OrderBy(l => l.TargetSkuCodeSnapshot)
            .Select(l => new ExchangeFulfillmentLineDto(l.TargetSkuId, l.TargetSkuCodeSnapshot, l.TargetOptionLabelSnapshot, l.Quantity)).ToList());
}
