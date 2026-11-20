using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HighConcurrencyCommerce.Application.CarrierTracking;
using HighConcurrencyCommerce.Application.Fulfillment;
using HighConcurrencyCommerce.Application.Notifications;
using HighConcurrencyCommerce.Application.Vouchers;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HighConcurrencyCommerce.Infrastructure.Fulfillment;

public sealed class PostgresFulfillmentService(
    StoreDbContext db,
    TimeProvider timeProvider,
    INotificationService notifications,
    IVoucherLifecycleManager voucherLifecycle) : IFulfillmentService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<FulfillmentOrderDto>> GetWorkQueueAsync(CancellationToken ct = default)
    {
        var orders = await WorkQuery()
            .Where(x => (x.PaymentMethod == "COD" || x.PaymentMethod == "PAYOS")
                        && (x.OrderStatus == "CONFIRMED" || x.OrderStatus == "FULFILLING"))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .ToListAsync(ct);
        return orders.Select(Map).ToList();
    }

    public async Task<FulfillmentOrderDto> StartAsync(Guid staffId, Guid orderId, string correlationId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var order = await LockOrderAsync(orderId, ct);
        if (order is null || order.PaymentMethod is not ("COD" or "PAYOS")) throw NotFound();
        if (order.OrderStatus != "CONFIRMED") throw Invalid("Chỉ đơn CONFIRMED mới được tiếp nhận fulfillment.");
        if (await db.Shipments.AnyAsync(x => x.OrderId == orderId, ct)) throw Invalid("Đơn đã có Shipment.");
        var reservation = await db.StockReservations.AsNoTracking().SingleOrDefaultAsync(x => x.OrderId == orderId, ct);
        var payment = await db.Payments.AsNoTracking().SingleOrDefaultAsync(x => x.OrderId == orderId, ct);
        var expectedPaymentStatus = order.PaymentMethod == "COD" ? "COD_PENDING" : "SUCCESS";
        if (reservation?.ReservationStatus != "ALLOCATED" || payment?.PaymentStatus != expectedPaymentStatus)
            throw Invalid("Reservation/Payment chưa sẵn sàng để fulfillment.");

        var now = Now();
        db.Shipments.Add(new Shipment
        {
            Id = Guid.NewGuid(), OrderId = orderId, ShipmentStatus = "PENDING_PACKING",
            RedeliveryCount = 0, ReturnStockCompleted = false, CreatedAt = now, UpdatedAt = now
        });
        order.OrderStatus = "FULFILLING";
        order.UpdatedAt = now;
        AddAudit(staffId, "FULFILLMENT_STARTED", orderId,
            new { orderStatus = "CONFIRMED" }, new { orderStatus = "FULFILLING", shipmentStatus = "PENDING_PACKING" }, correlationId, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await LoadDtoAsync(orderId, ct);
    }

    public async Task<FulfillmentOrderDto> PackAsync(Guid staffId, Guid shipmentId, string correlationId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var (order, shipment) = await LockOrderAndShipmentAsync(shipmentId, ct);
        if (order is null || shipment is null) throw NotFound();
        if (order.PaymentMethod is not ("COD" or "PAYOS") || order.OrderStatus != "FULFILLING" || shipment.ShipmentStatus != "PENDING_PACKING")
            throw Invalid("Shipment không ở trạng thái chờ đóng gói.");
        var now = Now();
        shipment.ShipmentStatus = "PACKED";
        shipment.UpdatedAt = now;
        AddAudit(staffId, "SHIPMENT_PACKED", order.Id,
            new { shipmentStatus = "PENDING_PACKING" }, new { shipmentStatus = "PACKED" }, correlationId, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await LoadDtoAsync(order.Id, ct);
    }

    public async Task<FulfillmentOrderDto> HandoverAsync(Guid staffId, Guid shipmentId, string correlationId, CancellationToken ct = default)
        => await HandoverCoreAsync(staffId, shipmentId, null, null, correlationId, ct);

    public async Task<FulfillmentOrderDto> HandoverWithConsignmentAsync(
        Guid staffId, Guid shipmentId, Guid consignmentId, long expectedRevision,
        string correlationId, CancellationToken ct = default)
        => await HandoverCoreAsync(staffId, shipmentId, consignmentId, expectedRevision, correlationId, ct);

    private async Task<FulfillmentOrderDto> HandoverCoreAsync(
        Guid staffId, Guid shipmentId, Guid? requiredConsignmentId, long? expectedRevision,
        string correlationId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var (order, shipment) = await LockOrderAndShipmentAsync(shipmentId, ct);
        if (order is null || shipment is null) throw NotFound();
        var consignment = await LockConsignmentAsync(shipmentId, exchange: false, ct);
        ValidateRequiredConsignmentIdentity(consignment, requiredConsignmentId);
        if (shipment.ShipmentStatus == "SHIPPING" &&
            (consignment is null || consignment.ConsignmentStatus == "HANDED_OVER"))
        {
            await tx.CommitAsync(ct);
            return await LoadDtoAsync(order.Id, ct);
        }
        EnsureConsignmentRevision(consignment, expectedRevision);
        if (order.PaymentMethod is not ("COD" or "PAYOS") || order.OrderStatus != "FULFILLING" || shipment.ShipmentStatus != "PACKED")
            throw Invalid("Chỉ Shipment PACKED của đơn đang fulfillment mới được bàn giao.");
        if (consignment is not null && consignment.ConsignmentStatus != "LABEL_READY")
            throw new CarrierTrackingFailure("TRACKING_INVALID_TRANSITION",
                "Vận đơn phải có nhãn LABEL_READY trước khi bàn giao.");

        var reservation = await db.StockReservations.Include(x => x.StockReservationLines)
            .SingleOrDefaultAsync(x => x.OrderId == order.Id, ct);
        if (reservation?.ReservationStatus != "ALLOCATED") throw Invalid("Reservation không ở trạng thái ALLOCATED.");
        var skuIds = reservation.StockReservationLines.Select(x => x.ProductSkuId).OrderBy(x => x).ToArray();
        await LockSkuAndBalancesAsync(skuIds, ct);
        var balances = await db.InventoryBalances.Where(x => skuIds.Contains(x.ProductSkuId))
            .ToDictionaryAsync(x => x.ProductSkuId, ct);
        var now = Now();
        foreach (var line in reservation.StockReservationLines.OrderBy(x => x.ProductSkuId))
        {
            if (!balances.TryGetValue(line.ProductSkuId, out var balance)
                || balance.AllocatedQty < line.Quantity || balance.PhysicalQty < line.Quantity)
                throw new FulfillmentFailure("INVENTORY_INVARIANT", "Allocated/Physical không đủ để xuất kho.");
            balance.PhysicalQty -= line.Quantity;
            balance.AllocatedQty -= line.Quantity;
            balance.Revision += 1;
            balance.UpdatedAt = now;
            line.LineStatus = "CONSUMED";
            line.UpdatedAt = now;
            db.InventoryMovements.Add(new InventoryMovement
            {
                Id = Guid.NewGuid(), ProductSkuId = line.ProductSkuId, MovementType = "STOCK_OUT",
                QtyDelta = -line.Quantity, SourceType = "SALES_ORDER", SourceId = order.Id,
                ActorId = staffId, Note = "Bàn giao đơn vị vận chuyển", CreatedAt = now, UpdatedAt = now
            });
        }
        reservation.ReservationStatus = "CONSUMED";
        reservation.UpdatedAt = now;
        shipment.ShipmentStatus = "SHIPPING";
        shipment.ShippedAt = now;
        shipment.UpdatedAt = now;
        if (consignment is not null)
            TransitionConsignment(consignment, staffId, "HANDED_OVER", "handover", "HANDED_OVER",
                "Kiện hàng đã được bàn giao cho đơn vị vận chuyển.", now, correlationId);
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(), ShipmentId = shipment.Id, AttemptNo = 1, Result = "IN_PROGRESS",
            Note = "Lần giao đầu", CreatedAt = now, UpdatedAt = now
        });
        AddAudit(staffId, "SHIPMENT_HANDED_OVER", order.Id,
            new { shipmentStatus = "PACKED", reservationStatus = "ALLOCATED" },
            new { shipmentStatus = "SHIPPING", reservationStatus = "CONSUMED" }, correlationId, now);
        await notifications.EnsureAsync(order.CustomerId, $"order:{order.Id:N}:shipping:1", "ORDER_SHIPPING",
            "Đơn hàng đang được giao", $"Đơn {order.OrderNumber} đã rời kho và đang trên đường giao đến bạn.", ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await LoadDtoAsync(order.Id, ct);
    }

    public async Task<FulfillmentOrderDto> RecordDeliveryAsync(
        Guid staffId, Guid shipmentId, DeliveryResultRequest request, string correlationId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var (order, shipment) = await LockOrderAndShipmentAsync(shipmentId, ct);
        if (order is null || shipment is null) throw NotFound();
        var consignment = await LockConsignmentAsync(shipmentId, exchange: false, ct);
        if (order.PaymentMethod is not ("COD" or "PAYOS") || order.OrderStatus != "FULFILLING" || shipment.ShipmentStatus != "SHIPPING")
            throw Invalid("Chỉ ghi nhận kết quả cho Shipment đang SHIPPING.");
        var attempt = await db.DeliveryAttempts
            .Where(x => x.ShipmentId == shipmentId && x.Result == "IN_PROGRESS")
            .OrderByDescending(x => x.AttemptNo).FirstOrDefaultAsync(ct)
            ?? throw Invalid("Không có lần giao IN_PROGRESS.");
        var now = Now();

        if (request.Result == "FAILED")
        {
            if (string.IsNullOrWhiteSpace(request.FailureReason))
                throw InvalidArgument("Giao thất bại phải có lý do.");
            if (request.CodCollectedAmount is not null || !string.IsNullOrWhiteSpace(request.CollectionReference))
                throw InvalidArgument("Không được khai báo khoản thu COD khi giao thất bại.");
            attempt.Result = "FAILED";
            attempt.FinishedAt = now;
            attempt.Note = request.FailureReason.Trim();
            attempt.UpdatedAt = now;
            shipment.ShipmentStatus = "DELIVERY_FAILED";
            shipment.UpdatedAt = now;
            if (consignment is not null)
                TransitionConsignment(consignment, staffId, "DELIVERY_FAILED",
                    $"delivery-failed:{attempt.AttemptNo}", "DELIVERY_FAILED", attempt.Note, now, correlationId);
            AddAudit(staffId, "DELIVERY_ATTEMPT_FAILED", order.Id,
                new { shipmentStatus = "SHIPPING", attemptNo = attempt.AttemptNo },
                new { shipmentStatus = "DELIVERY_FAILED", attemptNo = attempt.AttemptNo, reason = attempt.Note }, correlationId, now);
            await notifications.EnsureAsync(order.CustomerId, $"order:{order.Id:N}:delivery-failed:{attempt.AttemptNo}", "DELIVERY_FAILED",
                "Giao hàng chưa thành công", $"Lần giao {attempt.AttemptNo} của đơn {order.OrderNumber} chưa thành công: {attempt.Note}.", ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return await LoadDtoAsync(order.Id, ct);
        }

        if (request.Result != "DELIVERED") throw InvalidArgument("Kết quả giao hàng không hợp lệ.");
        var payment = await db.Payments.SingleOrDefaultAsync(x => x.OrderId == order.Id, ct);
        if (payment is null) throw Invalid("Không tìm thấy Payment của đơn hàng.");
        string? reference = null;
        if (order.PaymentMethod == "COD")
        {
            if (request.CodCollectedAmount is null || string.IsNullOrWhiteSpace(request.CollectionReference))
                throw InvalidArgument("Giao thành công phải có số tiền và mã biên nhận COD.");
            if (payment.PaymentStatus != "COD_PENDING") throw Invalid("Payment không ở trạng thái COD_PENDING.");
            if (request.CodCollectedAmount != payment.ExpectedAmount)
                throw new FulfillmentFailure("COD_AMOUNT_MISMATCH", "Số tiền COD thực thu phải đúng bằng số tiền đơn hàng.");
            reference = request.CollectionReference.Trim();
            db.PaymentTransactions.Add(new PaymentTransaction
            {
                Id = Guid.NewGuid(), PaymentId = payment.Id, AttemptId = null, Provider = "COD",
                ProviderTransactionId = reference, Amount = request.CodCollectedAmount.Value,
                ProviderPaidAt = now, VerifiedAt = now, TransactionStatus = "VERIFIED",
                CreatedAt = now, UpdatedAt = now
            });
        }
        else
        {
            if (request.CodCollectedAmount is not null || !string.IsNullOrWhiteSpace(request.CollectionReference))
                throw InvalidArgument("Đơn PayOS đã thanh toán trước, không được ghi nhận khoản thu COD.");
            if (payment.PaymentStatus != "SUCCESS") throw Invalid("Payment PayOS chưa ở trạng thái SUCCESS.");
        }
        attempt.Result = "DELIVERED";
        attempt.FinishedAt = now;
        attempt.Note = order.PaymentMethod == "COD"
            ? $"Đã giao và thu COD; ref={reference}"
            : "Đã giao đơn thanh toán trước qua PayOS";
        attempt.UpdatedAt = now;
        shipment.ShipmentStatus = "DELIVERED";
        shipment.DeliveredAt = now;
        shipment.UpdatedAt = now;
        if (order.PaymentMethod == "COD")
        {
            payment.PaymentStatus = "COD_COLLECTED";
            payment.PaidAt = now;
            payment.UpdatedAt = now;
        }
        order.OrderStatus = "COMPLETED";
        order.UpdatedAt = now;
        if (consignment is not null)
            TransitionConsignment(consignment, staffId, "DELIVERED", "delivered", "DELIVERED",
                order.PaymentMethod == "COD" ? "Đã giao hàng và hoàn tất thu COD." : "Đã giao hàng thanh toán trước qua PayOS.", now, correlationId);
        await voucherLifecycle.ConsumeForCompletedOrderAsync(order.Id, now, ct);
        AddAudit(staffId, order.PaymentMethod == "COD" ? "COD_DELIVERY_COMPLETED" : "PAYOS_DELIVERY_COMPLETED", order.Id,
            new { orderStatus = "FULFILLING", shipmentStatus = "SHIPPING", paymentStatus = order.PaymentMethod == "COD" ? "COD_PENDING" : "SUCCESS", voucherCode = order.VoucherCodeSnapshot, voucherDiscountAmount = order.VoucherDiscountAmount },
            new { orderStatus = "COMPLETED", shipmentStatus = "DELIVERED", paymentStatus = payment.PaymentStatus, collectionReference = reference, voucherCode = order.VoucherCodeSnapshot, voucherDiscountAmount = order.VoucherDiscountAmount },
            correlationId, now);
        await notifications.EnsureAsync(order.CustomerId, $"order:{order.Id:N}:delivered", "ORDER_DELIVERED",
            "Đơn hàng đã giao thành công", order.PaymentMethod == "COD"
                ? $"Đơn {order.OrderNumber} đã được giao và ghi nhận thanh toán COD."
                : $"Đơn {order.OrderNumber} thanh toán qua PayOS đã được giao thành công.", ct);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new FulfillmentFailure("COD_COLLECTION_REFERENCE_REUSED", "Mã tham chiếu thu COD đã được sử dụng.");
        }
        return await LoadDtoAsync(order.Id, ct);
    }

    public async Task<FulfillmentOrderDto> RetryDeliveryAsync(
        Guid staffId, Guid shipmentId, string correlationId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var (order, shipment) = await LockOrderAndShipmentAsync(shipmentId, ct);
        if (order is null || shipment is null) throw NotFound();
        var consignment = await LockConsignmentAsync(shipmentId, exchange: false, ct);
        if (order.PaymentMethod is not ("COD" or "PAYOS") || order.OrderStatus != "FULFILLING" || shipment.ShipmentStatus != "DELIVERY_FAILED" || shipment.ReturnStockCompleted)
            throw Invalid("Chỉ Shipment DELIVERY_FAILED chưa hoàn kho mới được giao lại.");
        if (shipment.RedeliveryCount >= 2)
            throw new FulfillmentFailure("REDELIVERY_LIMIT_REACHED", "Shipment đã sử dụng hết hai lần giao lại.");
        var currentAttemptNo = (short)(shipment.RedeliveryCount + 1);
        var failedAttemptExists = await db.DeliveryAttempts.AnyAsync(
            x => x.ShipmentId == shipmentId && x.AttemptNo == currentAttemptNo && x.Result == "FAILED", ct);
        if (!failedAttemptExists) throw Invalid("Lần giao hiện tại chưa kết thúc với kết quả FAILED.");

        var now = Now();
        shipment.RedeliveryCount += 1;
        shipment.ShipmentStatus = "SHIPPING";
        shipment.UpdatedAt = now;
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(), ShipmentId = shipment.Id, AttemptNo = (short)(shipment.RedeliveryCount + 1),
            Result = "IN_PROGRESS", Note = $"Giao lại lần {shipment.RedeliveryCount}", CreatedAt = now, UpdatedAt = now
        });
        if (consignment is not null)
            TransitionConsignment(consignment, staffId, "OUT_FOR_DELIVERY",
                $"redelivery-started:{shipment.RedeliveryCount}", "REDELIVERY_STARTED",
                $"Bắt đầu giao lại lần {shipment.RedeliveryCount}.", now, correlationId);
        AddAudit(staffId, "SHIPMENT_REDELIVERY_STARTED", order.Id,
            new { shipmentStatus = "DELIVERY_FAILED", redeliveryCount = shipment.RedeliveryCount - 1 },
            new { shipmentStatus = "SHIPPING", redeliveryCount = shipment.RedeliveryCount }, correlationId, now);
        await notifications.EnsureAsync(order.CustomerId, $"order:{order.Id:N}:shipping:{shipment.RedeliveryCount + 1}", "ORDER_REDELIVERY",
            "Đơn hàng đang được giao lại", $"Đơn {order.OrderNumber} bắt đầu lần giao số {shipment.RedeliveryCount + 1}.", ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await LoadDtoAsync(order.Id, ct);
    }

    public async Task<FulfillmentOrderDto> ReturnStockAsync(
        Guid staffId, Guid shipmentId, ReturnStockRequest request, string correlationId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var (order, shipment) = await LockOrderAndShipmentAsync(shipmentId, ct);
        if (order is null || shipment is null) throw NotFound();
        var consignment = await LockConsignmentAsync(shipmentId, exchange: false, ct);
        if (shipment.ReturnStockCompleted)
            throw new FulfillmentFailure("RETURN_STOCK_ALREADY_COMPLETED", "Shipment đã được hoàn kho.");
        if (order.PaymentMethod is not ("COD" or "PAYOS") || order.OrderStatus != "FULFILLING" || shipment.ShipmentStatus != "DELIVERY_FAILED")
            throw Invalid("Chỉ Shipment DELIVERY_FAILED mới được hoàn kho.");
        if (string.IsNullOrWhiteSpace(request.ReceivedEvidence))
            throw InvalidArgument("Hoàn kho phải có bằng chứng thực nhận hàng.");

        var reservation = await db.StockReservations.Include(x => x.StockReservationLines)
            .SingleOrDefaultAsync(x => x.OrderId == order.Id, ct);
        if (reservation?.ReservationStatus != "CONSUMED" || reservation.StockReservationLines.Any(x => x.LineStatus != "CONSUMED"))
            throw Invalid("Reservation chưa ở trạng thái CONSUMED.");
        var payment = await db.Payments.SingleOrDefaultAsync(x => x.OrderId == order.Id, ct);
        var expectedReturnPaymentStatus = order.PaymentMethod == "COD" ? "COD_PENDING" : "SUCCESS";
        if (payment?.PaymentStatus != expectedReturnPaymentStatus)
            throw Invalid("Payment không ở trạng thái hợp lệ để hoàn kho.");
        var now = Now();
        await voucherLifecycle.ReleaseForClosedOrderAsync(order.Id, "DELIVERY_FAILED_RETURNED", now, ct);
        await LockFlashSaleOffersForOrderAsync(order.Id, ct);
        var skuIds = reservation.StockReservationLines.Select(x => x.ProductSkuId).OrderBy(x => x).ToArray();
        await LockSkuAndBalancesAsync(skuIds, ct);
        var balances = await db.InventoryBalances.Where(x => skuIds.Contains(x.ProductSkuId))
            .ToDictionaryAsync(x => x.ProductSkuId, ct);
        foreach (var line in reservation.StockReservationLines.OrderBy(x => x.ProductSkuId))
        {
            if (!balances.TryGetValue(line.ProductSkuId, out var balance))
                throw new FulfillmentFailure("INVENTORY_INVARIANT", "Không tìm thấy số dư kho để hoàn hàng.");
            balance.PhysicalQty += line.Quantity;
            balance.AvailableQty += line.Quantity;
            balance.Revision += 1;
            balance.UpdatedAt = now;
            db.InventoryMovements.Add(new InventoryMovement
            {
                Id = Guid.NewGuid(), ProductSkuId = line.ProductSkuId, MovementType = "RETURN_IN",
                QtyDelta = line.Quantity, SourceType = "SALES_ORDER", SourceId = order.Id,
                ActorId = staffId, Note = request.ReceivedEvidence.Trim(), CreatedAt = now, UpdatedAt = now
            });
        }
        var orderItemIds = await db.SalesOrderItems
            .Where(oi => oi.OrderId == order.Id)
            .Select(oi => oi.Id)
            .ToListAsync(ct);
        var allocations = await db.FlashSaleAllocations
            .Include(a => a.Offer).ThenInclude(o => o.Campaign)
            .Where(a => orderItemIds.Contains(a.OrderItemId) && a.AllocationStatus == "SOLD")
            .ToListAsync(ct);

        foreach (var alloc in allocations)
        {
            alloc.AllocationStatus = "RELEASED";
            alloc.ReleasedAt = now;
            alloc.UpdatedAt = now;
            // SM-Q06: quota SOLD chỉ mở lại khi campaign còn ACTIVE
            if (alloc.Offer.Campaign.CampaignStatus == "ACTIVE" && now < alloc.Offer.Campaign.EndsAt)
            {
                alloc.Offer.QuotaSold = Math.Max(0, alloc.Offer.QuotaSold - alloc.Quantity);
                alloc.Offer.UpdatedAt = now;
            }
        }

        shipment.ReturnStockCompleted = true;
        shipment.UpdatedAt = now;
        payment.PaymentStatus = order.PaymentMethod == "COD" ? "VOID" : "REFUND_REQUIRED";
        payment.UpdatedAt = now;
        order.OrderStatus = "CANCELLED";
        order.ClosedReason = "DELIVERY_FAILED_RETURNED";
        order.UpdatedAt = now;
        if (consignment is not null)
            TransitionConsignment(consignment, staffId, "RETURNED", "returned", "RETURNED",
                "Kiện hàng giao thất bại đã được nhận lại và hoàn kho.", now, correlationId);
        AddAudit(staffId, "DELIVERY_RETURN_STOCK_COMPLETED", order.Id,
            new { orderStatus = "FULFILLING", shipmentStatus = "DELIVERY_FAILED", returnStockCompleted = false, voucherCode = order.VoucherCodeSnapshot, voucherDiscountAmount = order.VoucherDiscountAmount },
            new { orderStatus = "CANCELLED", closedReason = order.ClosedReason, returnStockCompleted = true, voucherCode = order.VoucherCodeSnapshot, voucherDiscountAmount = order.VoucherDiscountAmount }, correlationId, now);
        await notifications.EnsureAsync(order.CustomerId, $"order:{order.Id:N}:delivery-returned", "ORDER_CANCELLED",
            "Đơn hàng đã được đóng", $"Đơn {order.OrderNumber} đã được đóng sau khi giao không thành công và hàng đã về kho.", ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await LoadDtoAsync(order.Id, ct);
    }

    private async Task<SalesOrder?> LockOrderAsync(Guid orderId, CancellationToken ct) =>
        await db.SalesOrders.FromSqlInterpolated($"SELECT * FROM store.sales_orders WHERE id = {orderId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);

    private async Task<(SalesOrder? Order, Shipment? Shipment)> LockOrderAndShipmentAsync(Guid shipmentId, CancellationToken ct)
    {
        var orderId = await db.Shipments.AsNoTracking().Where(x => x.Id == shipmentId)
            .Select(x => (Guid?)x.OrderId).SingleOrDefaultAsync(ct);
        if (orderId is null) return (null, null);
        var order = await LockOrderAsync(orderId.Value, ct);
        var shipment = await db.Shipments.FromSqlInterpolated($"SELECT * FROM store.shipments WHERE id = {shipmentId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        return (order, shipment);
    }

    private async Task<CarrierConsignment?> LockConsignmentAsync(Guid shipmentId, bool exchange, CancellationToken ct) =>
        exchange
            ? await db.CarrierConsignments.FromSqlInterpolated(
                $"SELECT * FROM store.carrier_consignments WHERE exchange_shipment_id = {shipmentId} FOR UPDATE")
                .SingleOrDefaultAsync(ct)
            : await db.CarrierConsignments.FromSqlInterpolated(
                $"SELECT * FROM store.carrier_consignments WHERE sales_shipment_id = {shipmentId} FOR UPDATE")
                .SingleOrDefaultAsync(ct);

    private static void ValidateRequiredConsignmentIdentity(
        CarrierConsignment? consignment, Guid? requiredConsignmentId)
    {
        if (requiredConsignmentId is null) return;
        if (consignment is null || consignment.Id != requiredConsignmentId)
            throw new CarrierTrackingFailure("TRACKING_NOT_FOUND", "Không tìm thấy vận đơn cho shipment.", 404);
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

    private async Task LockSkuAndBalancesAsync(Guid[] skuIds, CancellationToken ct)
    {
        await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM store.product_skus WHERE id=ANY({skuIds}) ORDER BY id FOR UPDATE").ToListAsync(ct);
        await db.Database.SqlQuery<Guid>($"SELECT product_sku_id AS \"Value\" FROM store.inventory_balances WHERE product_sku_id=ANY({skuIds}) ORDER BY product_sku_id FOR UPDATE").ToListAsync(ct);
    }

    private async Task LockFlashSaleOffersForOrderAsync(Guid orderId, CancellationToken ct)
    {
        var offerIds = await (from allocation in db.FlashSaleAllocations.AsNoTracking()
                              join item in db.SalesOrderItems.AsNoTracking() on allocation.OrderItemId equals item.Id
                              where item.OrderId == orderId
                              orderby allocation.OfferId
                              select allocation.OfferId).Distinct().ToArrayAsync(ct);
        if (offerIds.Length > 0)
            await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM store.flash_sale_offers WHERE id=ANY({offerIds}) ORDER BY id FOR UPDATE").ToListAsync(ct);
    }

    private IQueryable<SalesOrder> WorkQuery() => db.SalesOrders.AsNoTracking()
        .Include(x => x.Customer).Include(x => x.Payment).Include(x => x.Shipment)
        .Include(x => x.SalesOrderItems);

    private async Task<FulfillmentOrderDto> LoadDtoAsync(Guid orderId, CancellationToken ct) =>
        Map(await WorkQuery().SingleAsync(x => x.Id == orderId, ct));

    private static FulfillmentOrderDto Map(SalesOrder order)
    {
        var address = JsonSerializer.Deserialize<AddressSnapshot>(order.ShippingAddressSnapshot, JsonOptions)
            ?? throw new InvalidOperationException("Order shipping snapshot is invalid.");
        return new FulfillmentOrderDto(order.Id, order.OrderNumber, order.OrderStatus,
            order.Shipment?.Id, order.Shipment?.ShipmentStatus, order.Payment?.PaymentStatus ?? "UNKNOWN",
            order.Customer.FullName, order.TotalAmount, address.RecipientName, address.Phone,
            string.Join(", ", new[] { address.AddressLine, address.Ward, address.Province }.Where(x => !string.IsNullOrWhiteSpace(x))),
            order.Shipment?.ShippedAt, order.Shipment?.DeliveredAt,
            order.Shipment?.RedeliveryCount ?? 0, order.Shipment?.ReturnStockCompleted ?? false,
            order.SalesOrderItems.OrderBy(x => x.ProductSkuId).Select(x => new FulfillmentItemDto(
                x.ProductSkuId, x.ProductTitleSnapshot, x.SkuCodeSnapshot, x.SkuOptionLabelSnapshot, x.Quantity)).ToList(),
            order.SubtotalAmount, order.VoucherDiscountAmount, order.VoucherCodeSnapshot,
            order.ShippingFee, order.ShippingMethodCodeSnapshot, order.ShippingMethodNameSnapshot,
            order.ShippingEstimatedMinDays, order.ShippingEstimatedMaxDays);
    }

    private void AddAudit(Guid staffId, string action, Guid orderId, object before, object after, string correlationId, DateTime now) =>
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(), ActorId = staffId, ActorType = "USER", Action = action,
            EntityType = "SalesOrder", EntityId = orderId,
            BeforeData = JsonSerializer.Serialize(before, JsonOptions), AfterData = JsonSerializer.Serialize(after, JsonOptions),
            CorrelationId = correlationId, OccurredAt = now, CreatedAt = now, UpdatedAt = now
        });

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;
    private static FulfillmentFailure NotFound() => new("NOT_FOUND", "Không tìm thấy đơn hoặc Shipment.", 404);
    private static FulfillmentFailure Invalid(string message) => new("INVALID_STATE", message);
    private static FulfillmentFailure InvalidArgument(string message) => new("INVALID_ARGUMENT", message, 400);
    private sealed record AddressSnapshot(string RecipientName, string Phone, string AddressLine, string? Ward, string Province);
}
