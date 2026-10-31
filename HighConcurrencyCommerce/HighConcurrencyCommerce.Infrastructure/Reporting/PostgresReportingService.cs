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
        var endExclusive = AsUtc(toDate.AddDays(1));
        var rows = await db.Payments.AsNoTracking()
            .Where(x => x.Order.OrderStatus == "COMPLETED"
                && (x.PaymentStatus == "SUCCESS" || x.PaymentStatus == "COD_COLLECTED")
                && x.PaidAt >= start && x.PaidAt < endExclusive
                && x.PaymentTransactions.Any(t => t.TransactionStatus == "VERIFIED" && t.Amount == x.ExpectedAmount))
            .Select(x => new { x.ExpectedAmount, x.PaymentMethod, PaidAt = x.PaidAt!.Value })
            .ToListAsync(ct);
        var revenue = rows.Sum(x => x.ExpectedAmount);
        var count = rows.Count;
        return new RevenueReportDto(fromDate, toDate, revenue, count, count == 0 ? 0 : revenue / count,
            rows.GroupBy(x => DateOnly.FromDateTime(x.PaidAt)).OrderBy(x => x.Key)
                .Select(x => new RevenueDayDto(x.Key, x.Sum(row => row.ExpectedAmount), x.Count())).ToList(),
            rows.GroupBy(x => x.PaymentMethod).OrderBy(x => x.Key)
                .Select(x => new RevenueMethodDto(x.Key, x.Sum(row => row.ExpectedAmount), x.Count())).ToList());
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
        return new StaffTaskSummaryDto(pendingCod, toPack, toHandover, deliveryFailures, lowStock, unread);
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
        if (from is not null) query = query.Where(x => x.OccurredAt >= AsUtc(from.Value));
        if (to is not null) query = query.Where(x => x.OccurredAt < AsUtc(to.Value.AddDays(1)));
        if (from is not null && to is not null) ValidateDateRange(from.Value, to.Value);
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
        var days = to.DayNumber - from.DayNumber + 1;
        if (days is < 1 or > 366)
            throw new ReportingFailure("INVALID_DATE_RANGE", "Khoảng ngày phải từ 1 đến 366 ngày.");
    }

    private static DateTime AsUtc(DateOnly date) =>
        DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
}
