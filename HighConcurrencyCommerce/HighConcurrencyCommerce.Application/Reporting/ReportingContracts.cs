namespace HighConcurrencyCommerce.Application.Reporting;

public sealed record RevenueDayDto(DateOnly Date, decimal Revenue, int CompletedOrderCount);
public sealed record RevenueMethodDto(string PaymentMethod, decimal Revenue, int CompletedOrderCount);
public sealed record RevenueReportDto(
    DateOnly From,
    DateOnly To,
    decimal Revenue,
    int CompletedOrderCount,
    decimal AverageOrderValue,
    IReadOnlyList<RevenueDayDto> Days,
    IReadOnlyList<RevenueMethodDto> PaymentMethods);

public sealed record LowStockItemDto(
    Guid ProductSkuId,
    string ProductTitle,
    string SkuCode,
    string OptionLabel,
    int AvailableQty,
    int PhysicalQty,
    int ReservedQty,
    int AllocatedQty);

public sealed record LowStockReportDto(int Threshold, IReadOnlyList<LowStockItemDto> Items);

public sealed record StaffTaskSummaryDto(
    int PendingCodCount,
    int ShipmentsToPackCount,
    int ShipmentsToHandoverCount,
    int DeliveryFailuresCount,
    int LowStockSkuCount,
    int NotificationsUnreadCount);

public sealed record AuditLogDto(
    Guid Id,
    Guid? ActorId,
    string ActorType,
    string? ActorName,
    string Action,
    string EntityType,
    Guid EntityId,
    string? BeforeData,
    string? AfterData,
    string? CorrelationId,
    DateTime OccurredAt);

public sealed record AuditLogPageDto(
    IReadOnlyList<AuditLogDto> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed class ReportingFailure(string code, string message, int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public sealed class ReportingOptions
{
    public int LowStockThreshold { get; set; } = 5;
}

public interface IReportingService
{
    Task<RevenueReportDto> GetRevenueAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default);
    Task<LowStockReportDto> GetLowStockAsync(CancellationToken ct = default);
    Task<StaffTaskSummaryDto> GetStaffTasksAsync(Guid staffId, CancellationToken ct = default);
    Task<AuditLogPageDto> GetAuditLogsAsync(
        string? action, string? entityType, Guid? actorId, DateOnly? from, DateOnly? to,
        int page, int pageSize, CancellationToken ct = default);
}
