using System;
using System.Collections.Generic;
using HighConcurrencyCommerce.Application.Returns;
using Xunit;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class RefundAllocationCalculatorTests
{
    private readonly RefundAllocationCalculator _calculator = new();

    [Fact]
    public void FullReturn_NoVoucher_ReturnsFullGross()
    {
        var item1Id = Guid.NewGuid();
        var item2Id = Guid.NewGuid();

        var context = new OrderRefundCalculationContext(
            OrderId: Guid.NewGuid(),
            SubtotalAmount: 300_000m,
            VoucherDiscountAmount: 0m,
            TotalAmount: 300_000m,
            OrderItems: new List<OrderItemRefundContext>
            {
                new(item1Id, Guid.NewGuid(), "Áo thun", "TSHIRT-01", "Size M", 2, 100_000m, 200_000m),
                new(item2Id, Guid.NewGuid(), "Quần jean", "JEAN-01", "Size 32", 1, 100_000m, 100_000m)
            });

        var selections = new List<ReturnItemSelection>
        {
            new(item1Id, 2),
            new(item2Id, 1)
        };

        var result = _calculator.Calculate(context, selections);

        Assert.Equal(2, result.Lines.Count);
        Assert.Equal(300_000m, result.TotalGrossAmount);
        Assert.Equal(0m, result.TotalAllocatedVoucherDiscount);
        Assert.Equal(300_000m, result.TotalRefundAmount);

        var line1 = Assert.Single(result.Lines, l => l.OrderItemId == item1Id);
        Assert.Equal(200_000m, line1.GrossAmount);
        Assert.Equal(0m, line1.AllocatedVoucherDiscount);
        Assert.Equal(200_000m, line1.RefundAmount);

        var line2 = Assert.Single(result.Lines, l => l.OrderItemId == item2Id);
        Assert.Equal(100_000m, line2.GrossAmount);
        Assert.Equal(0m, line2.AllocatedVoucherDiscount);
        Assert.Equal(100_000m, line2.RefundAmount);
    }

    [Fact]
    public void PartialReturn_NoVoucher_ReturnsProportionalGross()
    {
        var item1Id = Guid.NewGuid();
        var item2Id = Guid.NewGuid();

        var context = new OrderRefundCalculationContext(
            OrderId: Guid.NewGuid(),
            SubtotalAmount: 300_000m,
            VoucherDiscountAmount: 0m,
            TotalAmount: 300_000m,
            OrderItems: new List<OrderItemRefundContext>
            {
                new(item1Id, Guid.NewGuid(), "Áo thun", "TSHIRT-01", "Size M", 2, 100_000m, 200_000m),
                new(item2Id, Guid.NewGuid(), "Quần jean", "JEAN-01", "Size 32", 1, 100_000m, 100_000m)
            });

        // Customer only returns 1 of the 2 T-shirts
        var selections = new List<ReturnItemSelection>
        {
            new(item1Id, 1)
        };

        var result = _calculator.Calculate(context, selections);

        Assert.Single(result.Lines);
        Assert.Equal(100_000m, result.TotalGrossAmount);
        Assert.Equal(0m, result.TotalAllocatedVoucherDiscount);
        Assert.Equal(100_000m, result.TotalRefundAmount);

        var line1 = result.Lines[0];
        Assert.Equal(1, line1.RequestedQuantity);
        Assert.Equal(100_000m, line1.GrossAmount);
        Assert.Equal(0m, line1.AllocatedVoucherDiscount);
        Assert.Equal(100_000m, line1.RefundAmount);
    }

    [Fact]
    public void FullReturn_WithVoucher_ReturnsNetSubtotal()
    {
        var item1Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var item2Id = Guid.Parse("00000000-0000-0000-0000-000000000002");

        // Subtotal = 300,000; Voucher = 50,000; Total = 250,000
        // Item 1 gross: 200,000 (2/3 of 50,000 = 33,333.33 -> floor 33,333)
        // Item 2 gross: 100,000 (1/3 of 50,000 = 16,666.66 -> floor 16,666)
        // Sum floor = 49,999. Remainder = 1 VND.
        // Remainder goes to lowest OrderItemId (item1Id):
        // Item 1 voucher discount: 33,333 + 1 = 33,334 -> Net = 200,000 - 33,334 = 166,666
        // Item 2 voucher discount: 16,666 -> Net = 100,000 - 16,666 = 83,334
        // Total refund = 166,666 + 83,334 = 250,000 (Exact TotalAmount)
        var context = new OrderRefundCalculationContext(
            OrderId: Guid.NewGuid(),
            SubtotalAmount: 300_000m,
            VoucherDiscountAmount: 50_000m,
            TotalAmount: 250_000m,
            OrderItems: new List<OrderItemRefundContext>
            {
                new(item1Id, Guid.NewGuid(), "Áo thun", "TSHIRT-01", "Size M", 2, 100_000m, 200_000m),
                new(item2Id, Guid.NewGuid(), "Quần jean", "JEAN-01", "Size 32", 1, 100_000m, 100_000m)
            });

        var selections = new List<ReturnItemSelection>
        {
            new(item1Id, 2),
            new(item2Id, 1)
        };

        var result = _calculator.Calculate(context, selections);

        Assert.Equal(2, result.Lines.Count);
        Assert.Equal(300_000m, result.TotalGrossAmount);
        Assert.Equal(50_000m, result.TotalAllocatedVoucherDiscount);
        Assert.Equal(250_000m, result.TotalRefundAmount);

        var line1 = Assert.Single(result.Lines, l => l.OrderItemId == item1Id);
        Assert.Equal(200_000m, line1.GrossAmount);
        Assert.Equal(33_334m, line1.AllocatedVoucherDiscount);
        Assert.Equal(166_666m, line1.RefundAmount);

        var line2 = Assert.Single(result.Lines, l => l.OrderItemId == item2Id);
        Assert.Equal(100_000m, line2.GrossAmount);
        Assert.Equal(16_666m, line2.AllocatedVoucherDiscount);
        Assert.Equal(83_334m, line2.RefundAmount);
    }

    [Fact]
    public void PartialReturn_WithVoucher_FloorsProportionalNetLine()
    {
        var item1Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var item2Id = Guid.Parse("00000000-0000-0000-0000-000000000002");

        // Subtotal = 300,000; Voucher = 50,000; Total = 250,000
        // Item 1: orderedQty = 2, unitPrice = 100,000, netLine = 166,666
        // Partial return 1 unit of Item 1:
        // refundAmount = floor(166,666 * 1 / 2) = 83,333
        // lineGross = 100,000
        // lineAllocatedVoucherDiscount = 100,000 - 83,333 = 16,667
        var context = new OrderRefundCalculationContext(
            OrderId: Guid.NewGuid(),
            SubtotalAmount: 300_000m,
            VoucherDiscountAmount: 50_000m,
            TotalAmount: 250_000m,
            OrderItems: new List<OrderItemRefundContext>
            {
                new(item1Id, Guid.NewGuid(), "Áo thun", "TSHIRT-01", "Size M", 2, 100_000m, 200_000m),
                new(item2Id, Guid.NewGuid(), "Quần jean", "JEAN-01", "Size 32", 1, 100_000m, 100_000m)
            });

        var selections = new List<ReturnItemSelection>
        {
            new(item1Id, 1)
        };

        var result = _calculator.Calculate(context, selections);

        Assert.Single(result.Lines);
        Assert.Equal(100_000m, result.TotalGrossAmount);
        Assert.Equal(16_667m, result.TotalAllocatedVoucherDiscount);
        Assert.Equal(83_333m, result.TotalRefundAmount);

        var line = result.Lines[0];
        Assert.Equal(item1Id, line.OrderItemId);
        Assert.Equal(1, line.RequestedQuantity);
        Assert.Equal(100_000m, line.UnitPrice);
        Assert.Equal(100_000m, line.GrossAmount);
        Assert.Equal(16_667m, line.AllocatedVoucherDiscount);
        Assert.Equal(83_333m, line.RefundAmount);
    }

    [Fact]
    public void FullyDiscountedSelectedLine_IsRejectedInsteadOfManufacturingOneDongRefund()
    {
        var fullyDiscountedItemId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var paidItemId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var context = new OrderRefundCalculationContext(
            OrderId: Guid.NewGuid(),
            SubtotalAmount: 100m,
            VoucherDiscountAmount: 99m,
            TotalAmount: 1m,
            OrderItems: new List<OrderItemRefundContext>
            {
                new(fullyDiscountedItemId, Guid.NewGuid(), "Phụ kiện mẫu", "SAMPLE-01", "Default", 1, 1m, 1m),
                new(paidItemId, Guid.NewGuid(), "Sản phẩm chính", "MAIN-01", "Default", 1, 99m, 99m)
            });

        var ex = Assert.Throws<OrderReturnFailure>(() => _calculator.Calculate(
            context,
            new[] { new ReturnItemSelection(fullyDiscountedItemId, 1) }));

        Assert.Equal("ITEM_HAS_NO_REFUNDABLE_AMOUNT", ex.Code);
    }

    [Fact]
    public void Validation_ThrowsForEmptySelections()
    {
        var context = new OrderRefundCalculationContext(
            OrderId: Guid.NewGuid(),
            SubtotalAmount: 100_000m,
            VoucherDiscountAmount: 0m,
            TotalAmount: 100_000m,
            OrderItems: new List<OrderItemRefundContext>
            {
                new(Guid.NewGuid(), Guid.NewGuid(), "Item", "SKU", "Default", 1, 100_000m, 100_000m)
            });

        var ex = Assert.Throws<OrderReturnFailure>(() => _calculator.Calculate(context, Array.Empty<ReturnItemSelection>()));
        Assert.Equal("EMPTY_SELECTIONS", ex.Code);
    }

    [Fact]
    public void Validation_ThrowsForDuplicateSelections()
    {
        var itemId = Guid.NewGuid();
        var context = new OrderRefundCalculationContext(
            OrderId: Guid.NewGuid(),
            SubtotalAmount: 100_000m,
            VoucherDiscountAmount: 0m,
            TotalAmount: 100_000m,
            OrderItems: new List<OrderItemRefundContext>
            {
                new(itemId, Guid.NewGuid(), "Item", "SKU", "Default", 2, 50_000m, 100_000m)
            });

        var selections = new List<ReturnItemSelection>
        {
            new(itemId, 1),
            new(itemId, 1)
        };

        var ex = Assert.Throws<OrderReturnFailure>(() => _calculator.Calculate(context, selections));
        Assert.Equal("DUPLICATE_ITEM_SELECTION", ex.Code);
    }

    [Fact]
    public void Validation_ThrowsForUnknownItem()
    {
        var itemId = Guid.NewGuid();
        var context = new OrderRefundCalculationContext(
            OrderId: Guid.NewGuid(),
            SubtotalAmount: 100_000m,
            VoucherDiscountAmount: 0m,
            TotalAmount: 100_000m,
            OrderItems: new List<OrderItemRefundContext>
            {
                new(itemId, Guid.NewGuid(), "Item", "SKU", "Default", 1, 100_000m, 100_000m)
            });

        var unknownId = Guid.NewGuid();
        var selections = new List<ReturnItemSelection>
        {
            new(unknownId, 1)
        };

        var ex = Assert.Throws<OrderReturnFailure>(() => _calculator.Calculate(context, selections));
        Assert.Equal("INVALID_ORDER_ITEM", ex.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5)]
    public void Validation_ThrowsForInvalidQuantity(int requestedQty)
    {
        var itemId = Guid.NewGuid();
        var context = new OrderRefundCalculationContext(
            OrderId: Guid.NewGuid(),
            SubtotalAmount: 200_000m,
            VoucherDiscountAmount: 0m,
            TotalAmount: 200_000m,
            OrderItems: new List<OrderItemRefundContext>
            {
                new(itemId, Guid.NewGuid(), "Item", "SKU", "Default", 2, 100_000m, 200_000m)
            });

        var selections = new List<ReturnItemSelection>
        {
            new(itemId, requestedQty)
        };

        var ex = Assert.Throws<OrderReturnFailure>(() => _calculator.Calculate(context, selections));
        Assert.Equal("INVALID_REQUESTED_QUANTITY", ex.Code);
    }

    [Fact]
    public void FullReturn_WithShippingFee_RefundsMerchandiseOnly()
    {
        var itemId = Guid.NewGuid();
        var context = new OrderRefundCalculationContext(
            OrderId: Guid.NewGuid(),
            SubtotalAmount: 300_000m,
            VoucherDiscountAmount: 50_000m,
            TotalAmount: 280_000m,
            OrderItems: new List<OrderItemRefundContext>
            {
                new(itemId, Guid.NewGuid(), "Vợt", "RACKET-01", "4U", 1, 300_000m, 300_000m)
            },
            ShippingFee: 30_000m);

        var result = _calculator.Calculate(context, new[] { new ReturnItemSelection(itemId, 1) });

        Assert.Equal(250_000m, result.TotalRefundAmount);
        Assert.NotEqual(context.TotalAmount, result.TotalRefundAmount);
    }
}

