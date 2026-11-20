using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HighConcurrencyCommerce.Application.Notifications;
using HighConcurrencyCommerce.Application.Returns;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HighConcurrencyCommerce.Infrastructure.Returns;

public sealed class PostgresOrderReturnCustomerService(
    StoreDbContext db,
    IRefundAllocationCalculator calculator,
    INotificationService notifications,
    TimeProvider timeProvider) : IOrderReturnCustomerService
{
    public async Task<OrderReturnEligibilityDto> GetEligibilityAsync(
        Guid customerId,
        Guid orderId,
        CancellationToken ct = default)
    {
        var order = await db.SalesOrders
            .AsNoTracking()
            .Include(o => o.Shipment)
            .Include(o => o.Payment)
            .Include(o => o.SalesOrderItems)
                .ThenInclude(i => i.ProductSku)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);

        if (order == null || order.CustomerId != customerId)
        {
            throw new OrderReturnFailure("ORDER_NOT_FOUND", "Không tìm thấy đơn hàng.", 404);
        }

        var existingReturn = await db.OrderReturnRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.OrderId == orderId, ct);

        if (existingReturn != null)
        {
            return new OrderReturnEligibilityDto(
                false,
                "Đơn hàng đã có yêu cầu trả hàng.",
                order.Shipment?.DeliveredAt,
                order.Shipment?.DeliveredAt?.AddDays(7),
                Array.Empty<EligibleOrderItemDto>(),
                order.ShippingFee,
                order.TotalAmount);
        }

        if (!string.Equals(order.PaymentMethod, "COD", StringComparison.OrdinalIgnoreCase))
        {
            return new OrderReturnEligibilityDto(
                false,
                "Chỉ áp dụng cho đơn hàng thanh toán khi nhận hàng (COD).",
                order.Shipment?.DeliveredAt,
                null,
                Array.Empty<EligibleOrderItemDto>(),
                order.ShippingFee,
                order.TotalAmount);
        }

        if (!string.Equals(order.OrderStatus, "COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            return new OrderReturnEligibilityDto(
                false,
                "Đơn hàng chưa hoàn thành.",
                order.Shipment?.DeliveredAt,
                null,
                Array.Empty<EligibleOrderItemDto>(),
                order.ShippingFee,
                order.TotalAmount);
        }

        if (order.Shipment == null || !string.Equals(order.Shipment.ShipmentStatus, "DELIVERED", StringComparison.OrdinalIgnoreCase) || order.Shipment.DeliveredAt == null)
        {
            return new OrderReturnEligibilityDto(
                false,
                "Đơn hàng chưa được giao thành công.",
                null,
                null,
                Array.Empty<EligibleOrderItemDto>(),
                order.ShippingFee,
                order.TotalAmount);
        }

        if (order.Payment == null || !string.Equals(order.Payment.PaymentStatus, "COD_COLLECTED", StringComparison.OrdinalIgnoreCase))
        {
            return new OrderReturnEligibilityDto(
                false,
                "Đơn hàng chưa được thu tiền COD.",
                order.Shipment.DeliveredAt,
                null,
                Array.Empty<EligibleOrderItemDto>(),
                order.ShippingFee,
                order.TotalAmount);
        }

        var deliveredAt = order.Shipment.DeliveredAt.Value;
        var windowExpiresAt = deliveredAt.AddDays(7);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        if (now < deliveredAt || now > windowExpiresAt)
        {
            return new OrderReturnEligibilityDto(
                false,
                "Đã quá thời hạn 7 ngày đổi/trả hàng kể từ khi nhận hàng.",
                deliveredAt,
                windowExpiresAt,
                Array.Empty<EligibleOrderItemDto>(),
                order.ShippingFee,
                order.TotalAmount);
        }

        var calcContext = new OrderRefundCalculationContext(
            OrderId: order.Id,
            SubtotalAmount: order.SubtotalAmount,
            VoucherDiscountAmount: order.VoucherDiscountAmount,
            TotalAmount: order.TotalAmount,
            OrderItems: order.SalesOrderItems.Select(oi => new OrderItemRefundContext(
                oi.Id,
                oi.ProductSkuId,
                oi.ProductTitleSnapshot,
                oi.SkuCodeSnapshot,
                oi.SkuOptionLabelSnapshot,
                oi.Quantity,
                oi.UnitPrice,
                oi.LineTotal ?? (oi.Quantity * oi.UnitPrice))).ToList(),
            ShippingFee: order.ShippingFee);

        var fullSelections = order.SalesOrderItems.Select(oi => new ReturnItemSelection(oi.Id, oi.Quantity)).ToList();
        var calcResult = calculator.Calculate(calcContext, fullSelections);
        var maxRefundMap = calcResult.Lines.ToDictionary(l => l.OrderItemId, l => l.RefundAmount);

        var productIds = order.SalesOrderItems.Select(i => i.ProductSku.ProductId).Distinct().ToArray();
        var exchangeTargets = await db.ProductSkus.AsNoTracking()
            .Where(s => productIds.Contains(s.ProductId)
                && s.SkuStatus == "ACTIVE"
                && s.Product.PublicationStatus == "PUBLISHED")
            .Select(s => new
            {
                s.Id,
                s.ProductId,
                s.SkuCode,
                s.OptionLabel,
                s.RegularPrice,
                AvailableQty = s.InventoryBalance == null ? 0 : s.InventoryBalance.AvailableQty
            })
            .OrderBy(s => s.SkuCode)
            .ToListAsync(ct);

        var eligibleItems = order.SalesOrderItems
            .OrderBy(oi => oi.Id)
            .Select(oi => new EligibleOrderItemDto(
                oi.Id,
                oi.ProductSkuId,
                oi.ProductTitleSnapshot,
                oi.SkuCodeSnapshot,
                oi.SkuOptionLabelSnapshot,
                oi.Quantity,
                oi.UnitPrice,
                oi.LineTotal ?? (oi.Quantity * oi.UnitPrice),
                maxRefundMap.GetValueOrDefault(oi.Id, oi.LineTotal ?? (oi.Quantity * oi.UnitPrice)),
                exchangeTargets
                    .Where(s => s.ProductId == oi.ProductSku.ProductId && s.Id != oi.ProductSkuId)
                    .Select(s => new ExchangeTargetSkuDto(s.Id, s.SkuCode, s.OptionLabel, s.RegularPrice, s.AvailableQty))
                    .ToList()))
            .ToList();

        return new OrderReturnEligibilityDto(
            true,
            null,
            deliveredAt,
            windowExpiresAt,
            eligibleItems,
            order.ShippingFee,
            order.TotalAmount);
    }

    public async Task<CustomerOrderReturnDetailDto> CreateReturnRequestAsync(
        Guid customerId,
        Guid orderId,
        CreateOrderReturnRequest request,
        string correlationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!OrderReturnReasons.IsValid(request.ReasonCode))
        {
            throw new OrderReturnFailure("INVALID_REASON_CODE", "Lý do trả hàng không hợp lệ.", 400);
        }

        if (!OrderReturnResolutionTypes.IsValid(request.ResolutionType))
        {
            throw new OrderReturnFailure("INVALID_RESOLUTION_TYPE", "Hình thức xử lý chỉ có thể là RETURN hoặc EXCHANGE.", 400);
        }

        var isExchange = string.Equals(request.ResolutionType, OrderReturnResolutionTypes.Exchange, StringComparison.OrdinalIgnoreCase);

        if (string.Equals(request.ReasonCode, OrderReturnReasons.Other, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(request.CustomerNote))
        {
            throw new OrderReturnFailure("NOTE_REQUIRED_FOR_OTHER", "Lý do KHÁC bắt buộc phải có ghi chú của khách hàng.", 400);
        }

        if (request.Items == null || request.Items.Count == 0)
        {
            throw new OrderReturnFailure("EMPTY_SELECTIONS", "Phải chọn ít nhất 1 sản phẩm để trả.", 400);
        }

        var distinctItemIds = new HashSet<Guid>();
        foreach (var item in request.Items)
        {
            if (!distinctItemIds.Add(item.OrderItemId))
            {
                throw new OrderReturnFailure("DUPLICATE_ITEM_SELECTION", "Sản phẩm chọn trả bị trùng lặp.", 400);
            }

            if (isExchange != item.TargetSkuId.HasValue)
            {
                throw new OrderReturnFailure("INVALID_EXCHANGE_SELECTION", isExchange
                    ? "Mỗi dòng đổi hàng phải chọn một SKU thay thế."
                    : "Yêu cầu hoàn tiền không được chứa SKU thay thế.", 400);
            }
        }

        if (isExchange && request.Items.Select(item => item.TargetSkuId).Distinct().Count() != request.Items.Count)
        {
            throw new OrderReturnFailure("DUPLICATE_EXCHANGE_TARGET", "Mỗi SKU thay thế chỉ được chọn cho một dòng trong cùng yêu cầu đổi hàng.", 400);
        }

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var order = await db.SalesOrders
            .FromSqlInterpolated($"SELECT * FROM store.sales_orders WHERE id = {orderId} FOR UPDATE")
            .Include(o => o.Shipment)
            .Include(o => o.Payment)
            .Include(o => o.SalesOrderItems)
                .ThenInclude(i => i.ProductSku)
            .FirstOrDefaultAsync(ct);

        if (order == null || order.CustomerId != customerId)
        {
            throw new OrderReturnFailure("ORDER_NOT_FOUND", "Không tìm thấy đơn hàng.", 404);
        }

        var returnExists = await db.OrderReturnRequests.AnyAsync(r => r.OrderId == orderId, ct);
        if (returnExists)
        {
            throw new OrderReturnFailure("RETURN_REQUEST_EXISTS", "Đơn hàng đã có yêu cầu trả hàng.", 409);
        }

        if (!string.Equals(order.PaymentMethod, "COD", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(order.OrderStatus, "COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            throw new OrderReturnFailure("RETURN_NOT_ELIGIBLE", "Chỉ áp dụng cho đơn hàng COD đã hoàn thành.", 400);
        }

        if (order.Shipment == null || !string.Equals(order.Shipment.ShipmentStatus, "DELIVERED", StringComparison.OrdinalIgnoreCase) ||
            order.Shipment.DeliveredAt == null)
        {
            throw new OrderReturnFailure("RETURN_NOT_ELIGIBLE", "Đơn hàng chưa được giao thành công.", 400);
        }

        if (order.Payment == null || !string.Equals(order.Payment.PaymentStatus, "COD_COLLECTED", StringComparison.OrdinalIgnoreCase))
        {
            throw new OrderReturnFailure("RETURN_NOT_ELIGIBLE", "Đơn hàng chưa được thu tiền COD.", 400);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var deliveredAt = order.Shipment.DeliveredAt.Value;
        if (now < deliveredAt || now > deliveredAt.AddDays(7))
        {
            throw new OrderReturnFailure("RETURN_WINDOW_EXPIRED", "Đã quá thời hạn 7 ngày đổi/trả hàng kể từ khi nhận hàng.", 400);
        }

        var calcContext = new OrderRefundCalculationContext(
            OrderId: order.Id,
            SubtotalAmount: order.SubtotalAmount,
            VoucherDiscountAmount: order.VoucherDiscountAmount,
            TotalAmount: order.TotalAmount,
            OrderItems: order.SalesOrderItems.Select(oi => new OrderItemRefundContext(
                oi.Id,
                oi.ProductSkuId,
                oi.ProductTitleSnapshot,
                oi.SkuCodeSnapshot,
                oi.SkuOptionLabelSnapshot,
                oi.Quantity,
                oi.UnitPrice,
                oi.LineTotal ?? (oi.Quantity * oi.UnitPrice))).ToList(),
            ShippingFee: order.ShippingFee);

        var selections = request.Items.Select(x => new ReturnItemSelection(x.OrderItemId, x.RequestedQuantity)).ToList();
        var calcResult = calculator.Calculate(calcContext, selections);

        var targetSkuById = new Dictionary<Guid, ProductSku>();
        if (isExchange)
        {
            var targetIds = request.Items.Select(i => i.TargetSkuId!.Value).Distinct().OrderBy(id => id).ToArray();
            await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM store.product_skus WHERE id = ANY({targetIds}) ORDER BY id FOR UPDATE").ToListAsync(ct);
            targetSkuById = await db.ProductSkus
                .Include(s => s.Product)
                .Where(s => targetIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, ct);

            var orderItems = order.SalesOrderItems.ToDictionary(i => i.Id);
            foreach (var selection in request.Items)
            {
                if (!orderItems.TryGetValue(selection.OrderItemId, out var source)
                    || !targetSkuById.TryGetValue(selection.TargetSkuId!.Value, out var target)
                    || target.Id == source.ProductSkuId
                    || target.ProductId != source.ProductSku.ProductId
                    || target.SkuStatus != "ACTIVE"
                    || target.Product.PublicationStatus != "PUBLISHED")
                {
                    throw new OrderReturnFailure("INVALID_EXCHANGE_TARGET", "SKU thay thế phải đang bán và thuộc cùng sản phẩm với SKU đã mua.", 400);
                }
            }
        }

        var returnRequestId = Guid.NewGuid();
        var returnRequest = new OrderReturnRequest
        {
            Id = returnRequestId,
            OrderId = order.Id,
            CustomerId = customerId,
            ResolutionType = isExchange ? OrderReturnResolutionTypes.Exchange : OrderReturnResolutionTypes.Return,
            ReturnStatus = OrderReturnStatuses.Requested,
            ReasonCode = request.ReasonCode,
            CustomerNote = string.IsNullOrWhiteSpace(request.CustomerNote) ? null : request.CustomerNote.Trim(),
            RefundAmount = calcResult.TotalRefundAmount,
            Revision = 0,
            RequestedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            Lines = new List<OrderReturnRequestLine>()
        };

        foreach (var calculated in calcResult.Lines)
        {
            var line = new OrderReturnRequestLine
            {
                Id = Guid.NewGuid(), ReturnRequestId = returnRequestId, OrderItemId = calculated.OrderItemId,
                ProductSkuId = calculated.ProductSkuId, RequestedQty = calculated.RequestedQuantity,
                ProductTitleSnapshot = calculated.ProductTitleSnapshot, SkuCodeSnapshot = calculated.SkuCodeSnapshot,
                OptionLabelSnapshot = calculated.OptionLabelSnapshot, UnitPrice = calculated.UnitPrice,
                GrossAmount = calculated.GrossAmount, AllocatedVoucherDiscount = calculated.AllocatedVoucherDiscount,
                RefundAmount = calculated.RefundAmount, CreatedAt = now
            };
            returnRequest.Lines.Add(line);

            if (isExchange)
            {
                var selected = request.Items.Single(i => i.OrderItemId == calculated.OrderItemId);
                var target = targetSkuById[selected.TargetSkuId!.Value];
                var exchangeLine = new OrderExchangeLine
                {
                    Id = Guid.NewGuid(), ReturnRequestId = returnRequestId, ReturnRequestLineId = line.Id,
                    SourceSkuId = calculated.ProductSkuId, TargetSkuId = target.Id, Quantity = calculated.RequestedQuantity,
                    TargetSkuCodeSnapshot = target.SkuCode, TargetOptionLabelSnapshot = target.OptionLabel,
                    TargetRegularPriceSnapshot = target.RegularPrice, CreatedAt = now, UpdatedAt = now
                };
                line.ExchangeLine = exchangeLine;
                returnRequest.ExchangeLines.Add(exchangeLine);
            }
        }

        db.OrderReturnRequests.Add(returnRequest);

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = customerId,
            ActorType = "USER",
            Action = isExchange ? "EXCHANGE_REQUESTED" : "RETURN_REQUESTED",
            EntityType = "OrderReturnRequest",
            EntityId = returnRequestId,
            AfterData = JsonSerializer.Serialize(new
            {
                orderId = order.Id,
                orderNumber = order.OrderNumber,
                refundAmount = calcResult.TotalRefundAmount,
                resolutionType = returnRequest.ResolutionType,
                reasonCode = request.ReasonCode,
                itemCount = calcResult.Lines.Count
            }),
            CorrelationId = correlationId,
            CreatedAt = now,
            UpdatedAt = now,
            OccurredAt = now
        });

        await notifications.EnsureAsync(
            customerId,
            $"order:{order.Id:N}:return:{returnRequestId:N}:requested",
            "RETURN_REQUESTED",
            "Yêu cầu trả hàng đã được gửi",
            $"Yêu cầu đổi/trả cho đơn {order.OrderNumber} đã được tiếp nhận và đang chờ duyệt.",
            ct);

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is NpgsqlException npg && npg.SqlState == "23505")
        {
            throw new OrderReturnFailure("RETURN_REQUEST_EXISTS", "Đơn hàng đã có yêu cầu trả hàng.", 409);
        }

        return await GetCustomerReturnByIdInternalAsync(returnRequestId, ct);
    }

    public async Task<CustomerOrderReturnPageDto> GetCustomerReturnsAsync(
        Guid customerId,
        CustomerOrderReturnQuery query,
        CancellationToken ct = default)
    {
        var page = Math.Clamp(query.Page, 1, 100_000);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var queryable = db.OrderReturnRequests
            .AsNoTracking()
            .Include(r => r.Order)
            .Include(r => r.Lines)
            .Where(r => r.CustomerId == customerId);

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var normalizedStatus = query.Status.Trim().ToUpperInvariant();
            queryable = queryable.Where(r => r.ReturnStatus == normalizedStatus);
        }

        var totalCount = await queryable.CountAsync(ct);
        var skip = (page - 1) * pageSize;

        var items = await queryable
            .OrderByDescending(r => r.RequestedAt)
            .ThenByDescending(r => r.Id)
            .Skip(skip)
            .Take(pageSize)
            .Select(r => new CustomerOrderReturnSummaryDto(
                r.Id,
                r.OrderId,
                r.Order.OrderNumber,
                r.ResolutionType,
                r.ReturnStatus,
                r.ReasonCode,
                r.RefundAmount,
                r.Lines.Sum(l => l.RequestedQty),
                r.RequestedAt,
                r.UpdatedAt,
                r.ManualRefund != null ? r.ManualRefund.EvidenceReference : null))
            .ToListAsync(ct);

        return new CustomerOrderReturnPageDto(items, totalCount, page, pageSize);
    }

    public async Task<CustomerOrderReturnDetailDto> GetCustomerReturnByIdAsync(
        Guid customerId,
        Guid returnId,
        CancellationToken ct = default)
    {
        var ret = await db.OrderReturnRequests
            .AsNoTracking()
            .Include(r => r.Order)
            .Include(r => r.Lines)
                .ThenInclude(l => l.ExchangeLine)!
                .ThenInclude(e => e!.Allocation)
            .Include(r => r.ExchangeShipment)
            .Include(r => r.ManualRefund)
            .FirstOrDefaultAsync(r => r.Id == returnId, ct);

        if (ret == null || ret.CustomerId != customerId)
        {
            throw new OrderReturnFailure("RETURN_NOT_FOUND", "Không tìm thấy yêu cầu trả hàng.", 404);
        }

        return MapDetailDto(ret);
    }

    public async Task<CustomerOrderReturnDetailDto> CancelReturnRequestAsync(
        Guid customerId,
        Guid returnId,
        string correlationId,
        CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var existing = await db.OrderReturnRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == returnId, ct);

        if (existing == null || existing.CustomerId != customerId)
        {
            throw new OrderReturnFailure("RETURN_NOT_FOUND", "Không tìm thấy yêu cầu trả hàng.", 404);
        }

        var order = await db.SalesOrders
            .FromSqlInterpolated($"SELECT * FROM store.sales_orders WHERE id = {existing.OrderId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);

        var ret = await db.OrderReturnRequests
            .FromSqlInterpolated($"SELECT * FROM store.order_return_requests WHERE id = {returnId} FOR UPDATE")
            .Include(r => r.Order)
            .Include(r => r.Lines)
                .ThenInclude(l => l.ExchangeLine)!
                .ThenInclude(e => e!.Allocation)
            .Include(r => r.ExchangeShipment)
            .FirstOrDefaultAsync(ct);

        if (ret == null || ret.CustomerId != customerId)
        {
            throw new OrderReturnFailure("RETURN_NOT_FOUND", "Không tìm thấy yêu cầu trả hàng.", 404);
        }

        if (string.Equals(ret.ReturnStatus, OrderReturnStatuses.Cancelled, StringComparison.OrdinalIgnoreCase))
        {
            await tx.CommitAsync(ct);
            return MapDetailDto(ret);
        }

        if (!string.Equals(ret.ReturnStatus, OrderReturnStatuses.Requested, StringComparison.OrdinalIgnoreCase))
        {
            throw new OrderReturnFailure("INVALID_STATUS_TRANSITION", $"Không thể hủy yêu cầu ở trạng thái {ret.ReturnStatus}.", 400);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        ret.ReturnStatus = OrderReturnStatuses.Cancelled;
        ret.UpdatedAt = now;
        ret.Revision += 1;

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = customerId,
            ActorType = "USER",
            Action = "RETURN_CANCELLED",
            EntityType = "OrderReturnRequest",
            EntityId = returnId,
            CorrelationId = correlationId,
            CreatedAt = now,
            UpdatedAt = now,
            OccurredAt = now
        });

        await notifications.EnsureAsync(
            customerId,
            $"order:{ret.OrderId:N}:return:{ret.Id:N}:cancelled",
            "RETURN_CANCELLED",
            "Yêu cầu trả hàng đã được hủy",
            $"Yêu cầu đổi/trả cho đơn {ret.Order.OrderNumber} đã được hủy.",
            ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return MapDetailDto(ret);
    }

    private async Task<CustomerOrderReturnDetailDto> GetCustomerReturnByIdInternalAsync(Guid returnId, CancellationToken ct)
    {
        var ret = await db.OrderReturnRequests
            .AsNoTracking()
            .Include(r => r.Order)
            .Include(r => r.Lines)
            .Include(r => r.ManualRefund)
            .FirstAsync(r => r.Id == returnId, ct);

        return MapDetailDto(ret);
    }

    private static CustomerOrderReturnDetailDto MapDetailDto(OrderReturnRequest r) =>
        new(
            Id: r.Id,
            OrderId: r.OrderId,
            OrderNumber: r.Order.OrderNumber,
            ResolutionType: r.ResolutionType,
            ReturnStatus: r.ReturnStatus,
            ReasonCode: r.ReasonCode,
            CustomerNote: r.CustomerNote,
            RefundAmount: r.RefundAmount,
            RequestedAt: r.RequestedAt,
            DecidedAt: r.DecidedAt,
            DecisionNote: r.DecisionNote,
            ReceivedAt: r.ReceivedAt,
            RefundedAt: r.ManualRefund?.RefundedAt,
            EvidenceReference: r.ManualRefund?.EvidenceReference,
            Lines: r.Lines.OrderBy(l => l.OrderItemId).Select(l => new OrderReturnLineDto(
                l.Id,
                l.OrderItemId,
                l.ProductSkuId,
                l.ProductTitleSnapshot,
                l.SkuCodeSnapshot,
                l.OptionLabelSnapshot,
                l.RequestedQty,
                l.UnitPrice,
                l.GrossAmount,
                l.AllocatedVoucherDiscount,
                l.RefundAmount,
                l.RestockQty,
                l.QuarantineQty,
                l.DamagedQty,
                l.ScrappedQty,
                l.ExchangeLine == null ? null : new ExchangeLineDto(
                    l.ExchangeLine.Id,
                    l.ExchangeLine.TargetSkuId,
                    l.ExchangeLine.TargetSkuCodeSnapshot,
                    l.ExchangeLine.TargetOptionLabelSnapshot,
                    l.ExchangeLine.TargetRegularPriceSnapshot,
                    l.ExchangeLine.Quantity,
                    l.ExchangeLine.Allocation?.AllocationStatus,
                    l.ExchangeLine.Allocation?.ExpiresAt))).ToList(),
            ExchangeShipment: r.ExchangeShipment == null ? null : new ExchangeShipmentDto(
                r.ExchangeShipment.Id,
                r.ExchangeShipment.ShipmentStatus,
                r.ExchangeShipment.RedeliveryCount,
                r.ExchangeShipment.ShippedAt,
                r.ExchangeShipment.DeliveredAt),
            CreatedAt: r.CreatedAt,
            UpdatedAt: r.UpdatedAt,
            ShippingFee: r.Order.ShippingFee,
            OrderTotalAmount: r.Order.TotalAmount);
}
