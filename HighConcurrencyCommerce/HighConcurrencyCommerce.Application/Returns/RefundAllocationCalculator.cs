using System;
using System.Collections.Generic;
using System.Linq;

namespace HighConcurrencyCommerce.Application.Returns;

public sealed class RefundAllocationCalculator : IRefundAllocationCalculator
{
    public RefundCalculationResult Calculate(
        OrderRefundCalculationContext orderContext,
        IReadOnlyList<ReturnItemSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(orderContext);
        ArgumentNullException.ThrowIfNull(selections);

        if (selections.Count == 0)
        {
            throw new OrderReturnFailure("EMPTY_SELECTIONS", "Phải chọn ít nhất 1 sản phẩm để trả.");
        }

        if (orderContext.OrderItems == null || orderContext.OrderItems.Count == 0)
        {
            throw new OrderReturnFailure("ORDER_HAS_NO_ITEMS", "Đơn hàng không có sản phẩm.");
        }

        // Check for duplicate items in selections
        var distinctItemIds = new HashSet<Guid>();
        foreach (var selection in selections)
        {
            if (!distinctItemIds.Add(selection.OrderItemId))
            {
                throw new OrderReturnFailure("DUPLICATE_ITEM_SELECTION", "Sản phẩm chọn trả bị trùng lặp.");
            }
        }

        var orderItemsMap = orderContext.OrderItems.ToDictionary(x => x.OrderItemId);

        // Precalculate allocated voucher discount per order item
        // Rule: Sort order items by OrderItemId ASC
        var sortedOrderItems = orderContext.OrderItems.OrderBy(x => x.OrderItemId).ToList();
        var itemVoucherDiscounts = new Dictionary<Guid, decimal>();

        if (orderContext.VoucherDiscountAmount > 0 && orderContext.SubtotalAmount > 0)
        {
            decimal sumFloor = 0m;
            var floorDiscounts = new Dictionary<Guid, decimal>();

            foreach (var item in sortedOrderItems)
            {
                var itemGross = item.OrderedQuantity * item.UnitPrice;
                var rawDiscount = Math.Floor((itemGross / orderContext.SubtotalAmount) * orderContext.VoucherDiscountAmount);
                floorDiscounts[item.OrderItemId] = rawDiscount;
                sumFloor += rawDiscount;
            }

            var remainder = (int)Math.Max(0m, Math.Floor(orderContext.VoucherDiscountAmount - sumFloor));

            for (var i = 0; i < sortedOrderItems.Count; i++)
            {
                var item = sortedOrderItems[i];
                var discount = floorDiscounts[item.OrderItemId];
                if (i < remainder)
                {
                    discount += 1m;
                }
                itemVoucherDiscounts[item.OrderItemId] = discount;
            }
        }
        else
        {
            foreach (var item in sortedOrderItems)
            {
                itemVoucherDiscounts[item.OrderItemId] = 0m;
            }
        }

        var lines = new List<CalculatedReturnLine>(selections.Count);

        foreach (var selection in selections)
        {
            if (!orderItemsMap.TryGetValue(selection.OrderItemId, out var item))
            {
                throw new OrderReturnFailure("INVALID_ORDER_ITEM", $"Sản phẩm {selection.OrderItemId} không thuộc đơn hàng.");
            }

            if (selection.RequestedQuantity <= 0 || selection.RequestedQuantity > item.OrderedQuantity)
            {
                throw new OrderReturnFailure(
                    "INVALID_REQUESTED_QUANTITY",
                    $"Số lượng yêu cầu trả ({selection.RequestedQuantity}) không hợp lệ cho sản phẩm {item.ProductTitle}.");
            }

            var itemGross = item.OrderedQuantity * item.UnitPrice;
            var itemAllocatedVoucherDiscount = itemVoucherDiscounts[item.OrderItemId];
            var itemNet = itemGross - itemAllocatedVoucherDiscount;

            var lineGross = selection.RequestedQuantity * item.UnitPrice;
            decimal lineRefundAmount;

            if (selection.RequestedQuantity == item.OrderedQuantity)
            {
                lineRefundAmount = itemNet;
            }
            else
            {
                lineRefundAmount = Math.Floor((itemNet * selection.RequestedQuantity) / item.OrderedQuantity);
            }

            // A selected line can have zero net value when the deterministic voucher
            // remainder fully discounts a very small line. Never manufacture a 1 VND
            // refund merely to satisfy the persistence constraint.
            lineRefundAmount = Math.Floor(lineRefundAmount);
            if (lineRefundAmount <= 0)
            {
                throw new OrderReturnFailure(
                    "ITEM_HAS_NO_REFUNDABLE_AMOUNT",
                    $"Sản phẩm {item.ProductTitle} không còn giá trị hoàn tiền sau khi phân bổ khuyến mại.");
            }
            if (lineRefundAmount > lineGross)
            {
                lineRefundAmount = lineGross;
            }

            var lineAllocatedVoucherDiscount = lineGross - lineRefundAmount;
            if (lineAllocatedVoucherDiscount < 0)
            {
                lineAllocatedVoucherDiscount = 0m;
                lineRefundAmount = lineGross;
            }

            lines.Add(new CalculatedReturnLine(
                OrderItemId: item.OrderItemId,
                ProductSkuId: item.ProductSkuId,
                ProductTitleSnapshot: item.ProductTitle,
                SkuCodeSnapshot: item.SkuCode,
                OptionLabelSnapshot: item.OptionLabel,
                RequestedQuantity: selection.RequestedQuantity,
                UnitPrice: item.UnitPrice,
                GrossAmount: lineGross,
                AllocatedVoucherDiscount: lineAllocatedVoucherDiscount,
                RefundAmount: lineRefundAmount));
        }

        var totalGross = lines.Sum(l => l.GrossAmount);
        var totalDiscount = lines.Sum(l => l.AllocatedVoucherDiscount);
        var totalRefund = lines.Sum(l => l.RefundAmount);

        if (totalRefund <= 0)
        {
            throw new OrderReturnFailure("INVALID_REFUND_AMOUNT", "Số tiền hoàn trả phải lớn hơn 0.");
        }

        // Shipping is a fulfilled service charge and is not refundable by the
        // merchandise return workflow. Never use Order.TotalAmount as the cap,
        // because it includes ShippingFee from T48 onward.
        var maxRefundableMerchandise = orderContext.SubtotalAmount - orderContext.VoucherDiscountAmount;
        if (maxRefundableMerchandise <= 0)
            throw new OrderReturnFailure("INVALID_REFUND_AMOUNT", "Đơn hàng không còn giá trị hàng hóa có thể hoàn.");
        if (totalRefund > maxRefundableMerchandise)
        {
            var excess = totalRefund - maxRefundableMerchandise;
            totalRefund = maxRefundableMerchandise;
            var lastLine = lines[^1];
            var adjustedRefund = Math.Max(1m, lastLine.RefundAmount - excess);
            var adjustedDiscount = lastLine.GrossAmount - adjustedRefund;
            lines[^1] = lastLine with
            {
                RefundAmount = adjustedRefund,
                AllocatedVoucherDiscount = adjustedDiscount
            };
            totalDiscount = lines.Sum(l => l.AllocatedVoucherDiscount);
            totalRefund = lines.Sum(l => l.RefundAmount);
        }

        return new RefundCalculationResult(
            Lines: lines,
            TotalGrossAmount: totalGross,
            TotalAllocatedVoucherDiscount: totalDiscount,
            TotalRefundAmount: totalRefund);
    }
}

