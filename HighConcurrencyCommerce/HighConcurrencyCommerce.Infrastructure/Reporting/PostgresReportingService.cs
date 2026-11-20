using HighConcurrencyCommerce.Application.Reporting;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HighConcurrencyCommerce.Infrastructure.Reporting;

public sealed class PostgresReportingService(
    StoreDbContext db,
    TimeProvider timeProvider,
    IOptions<ReportingOptions> options) : IReportingService
{
    private int LowStockThreshold => Math.Clamp(options.Value.LowStockThreshold, 0, 1_000_000);

    public async Task<RevenueReportDto> GetRevenueAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var fromDate = from ?? today.AddDays(-29);
        var toDate = to ?? today;
        ValidateDateRange(fromDate, toDate);
        var start = AsUtc(fromDate);
        var endExclusive = toDate < DateOnly.MaxValue ? AsUtc(toDate.AddDays(1)) : DateTime.MaxValue;

        var collectedStatuses = new HashSet<string>
        {
            "SUCCESS", "COD_COLLECTED", "REFUND_REQUIRED", "PARTIALLY_REFUNDED", "REFUNDED"
        };

        var rows = await db.Payments.AsNoTracking()
            .Where(x => x.Order.OrderStatus == "COMPLETED"
                && collectedStatuses.Contains(x.PaymentStatus)
                && x.PaidAt >= start && x.PaidAt < endExclusive
                && x.PaymentTransactions.Any(t => t.TransactionStatus == "VERIFIED" && t.Amount == x.ExpectedAmount))
            .Select(x => new { x.ExpectedAmount, x.PaymentMethod, x.Order.ShippingFee, PaidAt = x.PaidAt!.Value })
            .ToListAsync(ct);

        var refunds = await db.ManualRefunds.AsNoTracking()
            .Where(r => r.RefundStatus == "REFUNDED"
                && r.RefundedAt != null
                && r.RefundedAt >= start && r.RefundedAt < endExclusive)
            .Select(r => new { r.Amount, RefundedAt = r.RefundedAt!.Value })
            .ToListAsync(ct);

        var grossRevenue = rows.Sum(x => x.ExpectedAmount);
        var shippingRevenue = rows.Sum(x => x.ShippingFee);
        var totalRefund = refunds.Sum(x => x.Amount);
        // Refunds are recognized by refund date while gross revenue is recognized by
        // payment date. A period can therefore legitimately have negative net revenue.
        var netRevenue = grossRevenue - totalRefund;
        var count = rows.Count;

        var refundsByDay = refunds
            .GroupBy(r => DateOnly.FromDateTime(r.RefundedAt))
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));

        var allDates = rows.Select(x => DateOnly.FromDateTime(x.PaidAt))
            .Concat(refunds.Select(r => DateOnly.FromDateTime(r.RefundedAt)))
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        var paymentsByDay = rows.GroupBy(x => DateOnly.FromDateTime(x.PaidAt))
            .ToDictionary(g => g.Key, g => new { Gross = g.Sum(x => x.ExpectedAmount), Count = g.Count() });

        var days = allDates.Select(date =>
        {
            var dayGross = paymentsByDay.TryGetValue(date, out var p) ? p.Gross : 0m;
            var dayCount = p?.Count ?? 0;
            var dayRefund = refundsByDay.GetValueOrDefault(date, 0m);
            var dayNet = dayGross - dayRefund;
            return new RevenueDayDto(date, dayNet, dayCount, dayGross, dayRefund, dayNet);
        }).ToList();

        var paymentMethods = rows.GroupBy(x => x.PaymentMethod).OrderBy(x => x.Key)
            .Select(x => new RevenueMethodDto(x.Key, x.Sum(row => row.ExpectedAmount), x.Count())).ToList();

        return new RevenueReportDto(
            From: fromDate,
            To: toDate,
            Revenue: netRevenue,
            CompletedOrderCount: count,
            AverageOrderValue: count == 0 ? 0 : netRevenue / count,
            Days: days,
            PaymentMethods: paymentMethods,
            GrossRevenue: grossRevenue,
            RefundAmount: totalRefund,
            NetRevenue: netRevenue,
            ShippingRevenue: shippingRevenue);
    }

    public async Task<LowStockReportDto> GetLowStockAsync(CancellationToken ct = default)
    {
        var threshold = LowStockThreshold;
        var items = await SellableSkuQuery()
            .Where(x => (x.InventoryBalance == null ? 0 : x.InventoryBalance.AvailableQty) <= threshold)
            .OrderBy(x => x.InventoryBalance == null ? 0 : x.InventoryBalance.AvailableQty)
            .ThenBy(x => x.SkuCode)
            .Select(x => new LowStockItemDto(x.Id, x.Product.Title, x.SkuCode, x.OptionLabel,
                x.InventoryBalance == null ? 0 : x.InventoryBalance.AvailableQty,
                x.InventoryBalance == null ? 0 : x.InventoryBalance.PhysicalQty,
                x.InventoryBalance == null ? 0 : x.InventoryBalance.ReservedQty,
                x.InventoryBalance == null ? 0 : x.InventoryBalance.AllocatedQty))
            .ToListAsync(ct);
        return new LowStockReportDto(threshold, items);
    }

    public async Task<StaffTaskSummaryDto> GetStaffTasksAsync(Guid staffId, CancellationToken ct = default)
    {
        var pendingCod = await db.SalesOrders.CountAsync(
            x => x.PaymentMethod == "COD" && x.OrderStatus == "PENDING_COD_CONFIRMATION", ct);
        var toPack = await db.Shipments.CountAsync(x => x.ShipmentStatus == "PENDING_PACKING", ct);
        var toHandover = await db.Shipments.CountAsync(x => x.ShipmentStatus == "PACKED", ct);
        var deliveryFailures = await db.Shipments.CountAsync(
            x => x.ShipmentStatus == "DELIVERY_FAILED" && !x.ReturnStockCompleted, ct);
        var threshold = LowStockThreshold;
        var lowStock = await SellableSkuQuery().CountAsync(
            x => (x.InventoryBalance == null ? 0 : x.InventoryBalance.AvailableQty) <= threshold, ct);
        var unread = await db.InAppNotifications.CountAsync(x => x.RecipientId == staffId && x.ReadAt == null, ct);
        var approvedReturns = await db.OrderReturnRequests.CountAsync(x => x.ReturnStatus == "APPROVED", ct);
        return new StaffTaskSummaryDto(pendingCod, toPack, toHandover, deliveryFailures, lowStock, unread, approvedReturns);
    }

    public async Task<AuditLogPageDto> GetAuditLogsAsync(
        string? action, string? entityType, Guid? actorId, DateOnly? from, DateOnly? to,
        int page, int pageSize, CancellationToken ct = default)
    {
        if (page is < 1 or > 1_000_000 || pageSize is < 1 or > 100)
            throw new ReportingFailure("INVALID_ARGUMENT", "page phải trong khoảng 1–1.000.000 và pageSize trong khoảng 1–100.");
        var query = db.AuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(x => x.Action == action.Trim());
        if (!string.IsNullOrWhiteSpace(entityType)) query = query.Where(x => x.EntityType == entityType.Trim());
        if (actorId is not null) query = query.Where(x => x.ActorId == actorId);
        if (from is not null && to is not null) ValidateDateRange(from.Value, to.Value);
        else if (to is not null && to.Value >= new DateOnly(9999, 12, 31))
            throw new ReportingFailure("INVALID_DATE_RANGE", "Ngày kết thúc vượt quá giới hạn hệ thống cho phép.");
        if (from is not null) query = query.Where(x => x.OccurredAt >= AsUtc(from.Value));
        if (to is not null)
        {
            var endUtc = to.Value < DateOnly.MaxValue ? AsUtc(to.Value.AddDays(1)) : DateTime.MaxValue;
            query = query.Where(x => x.OccurredAt < endUtc);
        }
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new AuditLogDto(x.Id, x.ActorId, x.ActorType, x.Actor == null ? null : x.Actor.FullName,
                x.Action, x.EntityType, x.EntityId, x.BeforeData, x.AfterData, x.CorrelationId, x.OccurredAt))
            .ToListAsync(ct);
        return new AuditLogPageDto(items, page, pageSize, total);
    }

    private IQueryable<ProductSku> SellableSkuQuery() => db.ProductSkus.AsNoTracking()
        .Where(x => x.SkuStatus == "ACTIVE" && x.Product.PublicationStatus == "PUBLISHED");

    private static void ValidateDateRange(DateOnly from, DateOnly to)
    {
        if (to >= new DateOnly(9999, 12, 31))
            throw new ReportingFailure("INVALID_DATE_RANGE", "Ngày kết thúc vượt quá giới hạn hệ thống cho phép.");
        var days = to.DayNumber - from.DayNumber + 1;
        if (days is < 1 or > 366)
            throw new ReportingFailure("INVALID_DATE_RANGE", "Khoảng ngày phải từ 1 đến 366 ngày.");
    }

    private static DateTime AsUtc(DateOnly date) =>
        DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
}
