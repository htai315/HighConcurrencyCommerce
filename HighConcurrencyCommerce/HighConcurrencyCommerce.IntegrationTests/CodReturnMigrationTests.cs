using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class CodReturnMigrationTests : IClassFixture<CatalogFixture>
{
    private readonly CatalogFixture fixture;

    public CodReturnMigrationTests(CatalogFixture fixture)
    {
        this.fixture = fixture;
    }

    private static async Task<UserAccount> CreateTestUserAsync(StoreDbContext db, string roleKey)
    {
        var role = await db.Roles.FirstAsync(r => r.RoleKey == roleKey);
        var user = new UserAccount
        {
            Id = Guid.NewGuid(),
            RoleId = role.Id,
            Email = $"{roleKey.ToLowerInvariant()}_{Guid.NewGuid():N}@example.com",
            PasswordHash = "TEST_HASH",
            FullName = $"{roleKey} User",
            EmailVerifiedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.UserAccounts.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task Migration011_CanBeAppliedMultipleTimesWithoutErrorAndPreservesData()
    {
        await using var connection = await fixture.OpenDatabaseAsync();

        // 1. Read embedded migration resource
        await using var stream = typeof(CatalogFixture).Assembly.GetManifestResourceStream("CodReturnsRefundsSchema.sql")
            ?? throw new InvalidOperationException("Missing CodReturnsRefundsSchema.sql");
        using var reader = new StreamReader(stream);
        var migrationSql = await reader.ReadToEndAsync();

        // 2. Insert dummy order and return request via EF Core
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        var customer = await CreateTestUserAsync(db, "CUSTOMER");
        var sku = await db.ProductSkus.Include(s => s.Product).FirstAsync(s => s.SkuStatus == "ACTIVE");

        var orderId = Guid.NewGuid();
        var order = new SalesOrder
        {
            Id = orderId,
            CustomerId = customer.Id,
            OrderNumber = $"ORD-RET-TEST-{Guid.NewGuid():N}"[..24],
            PaymentMethod = "COD",
            OrderStatus = "COMPLETED",
            ShippingAddressSnapshot = "{\"recipientName\":\"Test\",\"phone\":\"0901234567\",\"addressLine\":\"123 Test Street\",\"province\":\"Hanoi\"}",
            TotalAmount = 500000m,
            SubtotalAmount = 500000m,
            VoucherDiscountAmount = 0m,
            DeadlineAt = DateTime.UtcNow.AddHours(24),
            CreatedAt = DateTime.UtcNow.AddDays(-2),
            UpdatedAt = DateTime.UtcNow.AddDays(-2)
        };
        db.SalesOrders.Add(order);

        var orderItemId = Guid.NewGuid();
        var item = new SalesOrderItem
        {
            Id = orderItemId,
            OrderId = orderId,
            ProductSkuId = sku.Id,
            ProductTitleSnapshot = sku.Product.Title,
            SkuCodeSnapshot = sku.SkuCode,
            SkuOptionLabelSnapshot = sku.OptionLabel,
            Quantity = 2,
            UnitPrice = 250000m,
            LineTotal = 500000m,
            CreatedAt = DateTime.UtcNow.AddDays(-2),
            UpdatedAt = DateTime.UtcNow.AddDays(-2)
        };
        db.SalesOrderItems.Add(item);

        var returnId = Guid.NewGuid();
        var returnRequest = new OrderReturnRequest
        {
            Id = returnId,
            OrderId = orderId,
            CustomerId = customer.Id,
            ReturnStatus = "REQUESTED",
            ReasonCode = "WRONG_SIZE",
            CustomerNote = "Cần đổi kích cỡ khác",
            RequestedAt = DateTime.UtcNow.AddHours(-1),
            RefundAmount = 250000m,
            Revision = 0,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            UpdatedAt = DateTime.UtcNow.AddHours(-1)
        };
        db.OrderReturnRequests.Add(returnRequest);

        var returnLineId = Guid.NewGuid();
        var returnLine = new OrderReturnRequestLine
        {
            Id = returnLineId,
            ReturnRequestId = returnId,
            OrderItemId = orderItemId,
            ProductSkuId = sku.Id,
            RequestedQty = 1,
            ProductTitleSnapshot = sku.Product.Title,
            SkuCodeSnapshot = sku.SkuCode,
            OptionLabelSnapshot = sku.OptionLabel,
            UnitPrice = 250000m,
            GrossAmount = 250000m,
            AllocatedVoucherDiscount = 0m,
            RefundAmount = 250000m,
            CreatedAt = DateTime.UtcNow.AddHours(-1)
        };
        db.OrderReturnRequestLines.Add(returnLine);

        await db.SaveChangesAsync();

        // 3. Re-apply migration twice to verify upgrade and idempotency
        for (var i = 0; i < 2; i++)
        {
            await using var cmd = new NpgsqlCommand(migrationSql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        // 4. Verify data is preserved after re-application
        using var verifyScope = fixture.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<StoreDbContext>();

        var reloadedReturn = await verifyDb.OrderReturnRequests
            .Include(r => r.Lines)
            .Include(r => r.Order)
            .FirstOrDefaultAsync(r => r.Id == returnId);

        Assert.NotNull(reloadedReturn);
        Assert.Equal("REQUESTED", reloadedReturn.ReturnStatus);
        Assert.Equal("WRONG_SIZE", reloadedReturn.ReasonCode);
        Assert.Equal(250000m, reloadedReturn.RefundAmount);
        Assert.Single(reloadedReturn.Lines);
        Assert.Equal(1, reloadedReturn.Lines.First().RequestedQty);
        Assert.Equal(250000m, reloadedReturn.Lines.First().RefundAmount);
    }

    [Fact]
    public async Task OrderReturnRequest_DatabaseConstraints_EnforceLifecycleAndAmounts()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        var customer = await CreateTestUserAsync(db, "CUSTOMER");
        var admin = await CreateTestUserAsync(db, "ADMIN");
        var staff = await CreateTestUserAsync(db, "STAFF");
        var sku = await db.ProductSkus.Include(s => s.Product).FirstAsync(s => s.SkuStatus == "ACTIVE");

        var orderId = Guid.NewGuid();
        var order = new SalesOrder
        {
            Id = orderId,
            CustomerId = customer.Id,
            OrderNumber = $"ORD-CONSTR-{Guid.NewGuid():N}"[..24],
            PaymentMethod = "COD",
            OrderStatus = "COMPLETED",
            ShippingAddressSnapshot = "{\"recipientName\":\"Test\",\"phone\":\"0901234567\",\"addressLine\":\"123 Test Street\",\"province\":\"Hanoi\"}",
            TotalAmount = 300000m,
            SubtotalAmount = 300000m,
            VoucherDiscountAmount = 0m,
            DeadlineAt = DateTime.UtcNow.AddHours(24),
            CreatedAt = DateTime.UtcNow.AddDays(-2),
            UpdatedAt = DateTime.UtcNow.AddDays(-2)
        };
        db.SalesOrders.Add(order);

        var item = new SalesOrderItem
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            ProductSkuId = sku.Id,
            ProductTitleSnapshot = sku.Product.Title,
            SkuCodeSnapshot = sku.SkuCode,
            SkuOptionLabelSnapshot = sku.OptionLabel,
            Quantity = 1,
            UnitPrice = 300000m,
            LineTotal = 300000m,
            CreatedAt = DateTime.UtcNow.AddDays(-2),
            UpdatedAt = DateTime.UtcNow.AddDays(-2)
        };
        db.SalesOrderItems.Add(item);
        await db.SaveChangesAsync();

        // 1. OTHER reason without customer note fails constraint
        var invalidReq = new OrderReturnRequest
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            CustomerId = customer.Id,
            ReturnStatus = "REQUESTED",
            ReasonCode = "OTHER",
            CustomerNote = null, // Violates ck_return_requests_reason
            RequestedAt = DateTime.UtcNow,
            RefundAmount = 300000m
        };
        db.OrderReturnRequests.Add(invalidReq);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        // 2. Negative or fractional refund amount fails constraint
        var invalidAmountReq = new OrderReturnRequest
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            CustomerId = customer.Id,
            ReturnStatus = "REQUESTED",
            ReasonCode = "DEFECTIVE",
            CustomerNote = "Bị nứt cán vợt",
            RequestedAt = DateTime.UtcNow,
            RefundAmount = -100m // Violates ck_return_requests_refund_amount
        };
        db.OrderReturnRequests.Add(invalidAmountReq);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        // 3. REQUESTED status with decided_by set fails lifecycle constraint
        var invalidLifecycleReq = new OrderReturnRequest
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            CustomerId = customer.Id,
            ReturnStatus = "REQUESTED",
            ReasonCode = "DEFECTIVE",
            RequestedAt = DateTime.UtcNow,
            DecidedBy = admin.Id, // Cannot be decided while REQUESTED
            RefundAmount = 300000m
        };
        db.OrderReturnRequests.Add(invalidLifecycleReq);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        // 4. Valid lifecycle progression: REQUESTED -> APPROVED -> RECEIVED -> REFUNDED
        var validReq = new OrderReturnRequest
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            CustomerId = customer.Id,
            ReturnStatus = "REQUESTED",
            ReasonCode = "DEFECTIVE",
            CustomerNote = "Lỗi kỹ thuật",
            RequestedAt = DateTime.UtcNow.AddHours(-3),
            RefundAmount = 300000m,
            Revision = 0
        };
        db.OrderReturnRequests.Add(validReq);
        await db.SaveChangesAsync();

        // Transition to APPROVED
        validReq.ReturnStatus = "APPROVED";
        validReq.DecidedBy = admin.Id;
        validReq.DecidedAt = DateTime.UtcNow.AddHours(-2);
        validReq.DecisionNote = "Đủ điều kiện đổi trả theo chính sách";
        await db.SaveChangesAsync();

        // Transition to RECEIVED
        validReq.ReturnStatus = "RECEIVED";
        validReq.ReceivedBy = staff.Id;
        validReq.ReceivedAt = DateTime.UtcNow.AddHours(-1);
        validReq.ReceivedEvidence = "Đã kiểm tra tem mác và tình trạng tại kho";
        await db.SaveChangesAsync();

        // Transition to REFUNDED
        validReq.ReturnStatus = "REFUNDED";
        await db.SaveChangesAsync();

        var reloaded = await db.OrderReturnRequests.FindAsync(validReq.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("REFUNDED", reloaded.ReturnStatus);
    }

    [Fact]
    public async Task PaymentStatus_AllowsPartiallyRefunded_AndManualRefundLinksToReturn()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        var customer = await CreateTestUserAsync(db, "CUSTOMER");
        var admin = await CreateTestUserAsync(db, "ADMIN");
        var sku = await db.ProductSkus.Include(s => s.Product).FirstAsync(s => s.SkuStatus == "ACTIVE");

        var orderId = Guid.NewGuid();
        var order = new SalesOrder
        {
            Id = orderId,
            CustomerId = customer.Id,
            OrderNumber = $"ORD-PARTIAL-{Guid.NewGuid():N}"[..24],
            PaymentMethod = "COD",
            OrderStatus = "COMPLETED",
            ShippingAddressSnapshot = "{\"recipientName\":\"Test\",\"phone\":\"0901234567\",\"addressLine\":\"123 Test Street\",\"province\":\"Hanoi\"}",
            TotalAmount = 500000m,
            SubtotalAmount = 500000m,
            VoucherDiscountAmount = 0m,
            DeadlineAt = DateTime.UtcNow.AddHours(24),
            CreatedAt = DateTime.UtcNow.AddDays(-2),
            UpdatedAt = DateTime.UtcNow.AddDays(-2)
        };
        db.SalesOrders.Add(order);

        var paymentId = Guid.NewGuid();
        var payment = new Payment
        {
            Id = paymentId,
            OrderId = orderId,
            PaymentMethod = "COD",
            ExpectedAmount = 500000m,
            PaymentStatus = "COD_COLLECTED",
            PaidAt = DateTime.UtcNow.AddDays(-1)
        };
        db.Payments.Add(payment);

        var txId = Guid.NewGuid();
        var paymentTx = new PaymentTransaction
        {
            Id = txId,
            PaymentId = paymentId,
            Provider = "COD",
            Amount = 500000m,
            TransactionStatus = "VERIFIED",
            VerifiedAt = DateTime.UtcNow.AddDays(-1)
        };
        db.PaymentTransactions.Add(paymentTx);

        var returnId = Guid.NewGuid();
        var returnReq = new OrderReturnRequest
        {
            Id = returnId,
            OrderId = orderId,
            CustomerId = customer.Id,
            ReturnStatus = "REFUNDED",
            ReasonCode = "WRONG_SIZE",
            RequestedAt = DateTime.UtcNow.AddHours(-2),
            DecidedBy = admin.Id,
            DecidedAt = DateTime.UtcNow.AddHours(-1),
            ReceivedBy = admin.Id,
            ReceivedAt = DateTime.UtcNow.AddMinutes(-30),
            ReceivedEvidence = "Đã nhận hàng trả",
            RefundAmount = 250000m
        };
        db.OrderReturnRequests.Add(returnReq);

        // Update payment status to PARTIALLY_REFUNDED
        payment.PaymentStatus = "PARTIALLY_REFUNDED";

        var uniqueEvidence = $"REF-TEST-{Guid.NewGuid():N}"[..20];
        var manualRefund = new ManualRefund
        {
            Id = Guid.NewGuid(),
            TransactionId = txId,
            AdminId = admin.Id,
            Amount = 250000m,
            EvidenceReference = uniqueEvidence,
            RefundStatus = "REFUNDED",
            RefundedAt = DateTime.UtcNow,
            ReturnRequestId = returnId
        };
        db.ManualRefunds.Add(manualRefund);

        await db.SaveChangesAsync();

        // Verify query and navigation
        db.ChangeTracker.Clear();
        var savedRefund = await db.ManualRefunds
            .Include(m => m.ReturnRequest)
            .Include(m => m.Transaction)
            .FirstOrDefaultAsync(m => m.Id == manualRefund.Id);

        Assert.NotNull(savedRefund);
        Assert.Equal(returnId, savedRefund.ReturnRequestId);
        Assert.NotNull(savedRefund.ReturnRequest);
        Assert.Equal("REFUNDED", savedRefund.ReturnRequest.ReturnStatus);

        var savedPayment = await db.Payments.FindAsync(paymentId);
        Assert.NotNull(savedPayment);
        Assert.Equal("PARTIALLY_REFUNDED", savedPayment.PaymentStatus);

        // Duplicate evidence reference fails unique constraint
        var txId2 = Guid.NewGuid();
        var paymentTx2 = new PaymentTransaction
        {
            Id = txId2,
            PaymentId = paymentId,
            Provider = "COD",
            Amount = 250000m,
            TransactionStatus = "VERIFIED",
            VerifiedAt = DateTime.UtcNow
        };
        db.PaymentTransactions.Add(paymentTx2);
        await db.SaveChangesAsync();

        var duplicateEvidenceRefund = new ManualRefund
        {
            Id = Guid.NewGuid(),
            TransactionId = txId2,
            AdminId = admin.Id,
            Amount = 100000m,
            EvidenceReference = uniqueEvidence, // duplicate
            RefundStatus = "REFUNDED",
            RefundedAt = DateTime.UtcNow
        };
        db.ManualRefunds.Add(duplicateEvidenceRefund);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
