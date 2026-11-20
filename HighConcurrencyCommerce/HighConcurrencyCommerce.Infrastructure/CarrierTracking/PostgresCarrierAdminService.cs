using System.Text.Json;
using HighConcurrencyCommerce.Application.CarrierTracking;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HighConcurrencyCommerce.Infrastructure.CarrierTracking;

public sealed class PostgresCarrierAdminService(StoreDbContext db, TimeProvider timeProvider) : ICarrierAdminService
{
    public async Task<IReadOnlyList<CarrierSummaryDto>> GetCarriersAsync(
        string? query, string? status, CancellationToken ct = default)
    {
        var normalizedStatus = NormalizeStatusFilter(status);
        var normalizedQuery = NormalizeOptional(query);
        var carriers = db.Carriers.AsNoTracking().AsQueryable();
        if (normalizedStatus is not null)
            carriers = carriers.Where(x => x.CarrierStatus == normalizedStatus);
        if (normalizedQuery is not null)
        {
            var pattern = $"%{EscapeLike(normalizedQuery)}%";
            carriers = carriers.Where(x =>
                EF.Functions.ILike(x.Code, pattern, "\\") ||
                EF.Functions.ILike(x.Name, pattern, "\\"));
        }

        return await carriers
            .OrderByDescending(x => x.CarrierStatus == "ACTIVE")
            .ThenBy(x => x.Name)
            .ThenBy(x => x.Id)
            .Select(x => new CarrierSummaryDto(
                x.Id, x.Code, x.Name, x.Hotline, x.TrackingUrlTemplate,
                x.CarrierStatus, x.Revision, x.Consignments.Count, x.UpdatedAt))
            .ToListAsync(ct);
    }

    public async Task<CarrierDetailDto> GetCarrierAsync(Guid carrierId, CancellationToken ct = default) =>
        ToDetail(await db.Carriers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == carrierId, ct)
            ?? throw NotFound());

    public async Task<CarrierDetailDto> CreateAsync(
        Guid adminId, CreateCarrierRequest request, string correlationId, CancellationToken ct = default)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        var name = NormalizeName(request.Name);
        var hotline = NormalizeOptional(request.Hotline);
        var trackingUrl = NormalizeAndValidateTrackingUrl(request.TrackingUrlTemplate);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var carrier = new Carrier
        {
            Id = Guid.NewGuid(),
            Code = code,
            Name = name,
            Hotline = hotline,
            TrackingUrlTemplate = trackingUrl,
            CarrierStatus = "INACTIVE",
            Revision = 1,
            CreatedBy = adminId,
            UpdatedBy = adminId,
            CreatedAt = now,
            UpdatedAt = now
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Carriers.Add(carrier);
        db.AuditLogs.Add(Audit(adminId, "CARRIER_CREATED", carrier.Id, null,
            Snapshot(carrier), correlationId, now));
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw new CarrierTrackingFailure("CARRIER_CODE_CONFLICT", "Mã đơn vị vận chuyển đã tồn tại.", 409);
        }
        return ToDetail(carrier);
    }

    public async Task<CarrierDetailDto> UpdateAsync(
        Guid adminId, Guid carrierId, UpdateCarrierRequest request,
        string correlationId, CancellationToken ct = default)
    {
        var name = NormalizeName(request.Name);
        var hotline = NormalizeOptional(request.Hotline);
        var trackingUrl = NormalizeAndValidateTrackingUrl(request.TrackingUrlTemplate);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var carrier = await LockCarrierAsync(carrierId, ct);
        EnsureRevision(carrier, request.ExpectedRevision);
        var before = Snapshot(carrier);
        carrier.Name = name;
        carrier.Hotline = hotline;
        carrier.TrackingUrlTemplate = trackingUrl;
        Touch(carrier, adminId);
        db.AuditLogs.Add(Audit(adminId, "CARRIER_UPDATED", carrier.Id, before,
            Snapshot(carrier), correlationId, carrier.UpdatedAt));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDetail(carrier);
    }

    public Task<CarrierDetailDto> ActivateAsync(
        Guid adminId, Guid carrierId, long expectedRevision,
        string correlationId, CancellationToken ct = default) =>
        ChangeStatusAsync(adminId, carrierId, expectedRevision, "ACTIVE", "CARRIER_ACTIVATED", correlationId, ct);

    public Task<CarrierDetailDto> DeactivateAsync(
        Guid adminId, Guid carrierId, long expectedRevision,
        string correlationId, CancellationToken ct = default) =>
        ChangeStatusAsync(adminId, carrierId, expectedRevision, "INACTIVE", "CARRIER_DEACTIVATED", correlationId, ct);

    private async Task<CarrierDetailDto> ChangeStatusAsync(
        Guid adminId, Guid carrierId, long expectedRevision, string status, string action,
        string correlationId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var carrier = await LockCarrierAsync(carrierId, ct);
        EnsureRevision(carrier, expectedRevision);
        if (carrier.CarrierStatus == status)
        {
            await tx.CommitAsync(ct);
            return ToDetail(carrier);
        }

        var before = Snapshot(carrier);
        carrier.CarrierStatus = status;
        Touch(carrier, adminId);
        db.AuditLogs.Add(Audit(adminId, action, carrier.Id, before,
            Snapshot(carrier), correlationId, carrier.UpdatedAt));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDetail(carrier);
    }

    private async Task<Carrier> LockCarrierAsync(Guid carrierId, CancellationToken ct) =>
        await db.Carriers
            .FromSqlInterpolated($"SELECT * FROM store.carriers WHERE id = {carrierId} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw NotFound();

    private void Touch(Carrier carrier, Guid adminId)
    {
        carrier.Revision++;
        carrier.UpdatedBy = adminId;
        carrier.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
    }

    private static CarrierDetailDto ToDetail(Carrier carrier) => new(
        carrier.Id, carrier.Code, carrier.Name, carrier.Hotline, carrier.TrackingUrlTemplate,
        carrier.CarrierStatus, carrier.Revision, carrier.CreatedAt, carrier.UpdatedAt);

    private static void EnsureRevision(Carrier carrier, long expectedRevision)
    {
        if (carrier.Revision != expectedRevision)
            throw new CarrierTrackingFailure("CARRIER_STALE",
                $"Đơn vị vận chuyển đã thay đổi (revision hiện tại: {carrier.Revision}). Hãy tải lại dữ liệu.", 409);
    }

    private static string NormalizeName(string name)
    {
        var normalized = name.Trim();
        if (normalized.Length < 2)
            throw new CarrierTrackingFailure("VALIDATION_ERROR", "Tên đơn vị vận chuyển phải có ít nhất 2 ký tự.");
        return normalized;
    }

    private static string? NormalizeAndValidateTrackingUrl(string? value)
    {
        var normalized = NormalizeOptional(value);
        if (normalized is null) return null;
        if (!normalized.Contains("{trackingNumber}", StringComparison.Ordinal)
            || !Uri.TryCreate(normalized.Replace("{trackingNumber}", "TRACK123", StringComparison.Ordinal),
                UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(uri.Host))
            throw new CarrierTrackingFailure("CARRIER_TRACKING_URL_INVALID",
                "URL tracking phải dùng HTTPS và chứa placeholder {trackingNumber}.");
        return normalized;
    }

    private static string? NormalizeStatusFilter(string? status)
    {
        var normalized = NormalizeOptional(status)?.ToUpperInvariant();
        if (normalized is not null and not ("ACTIVE" or "INACTIVE"))
            throw new CarrierTrackingFailure("VALIDATION_ERROR", "Trạng thái carrier chỉ nhận ACTIVE hoặc INACTIVE.");
        return normalized;
    }

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static CarrierTrackingFailure NotFound() =>
        new("CARRIER_NOT_FOUND", "Không tìm thấy đơn vị vận chuyển.", 404);
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    private static string Snapshot(Carrier carrier) => JsonSerializer.Serialize(new
    {
        carrier.Id, carrier.Code, carrier.Name, carrier.Hotline, carrier.TrackingUrlTemplate,
        carrier.CarrierStatus, carrier.Revision
    });
    private static AuditLog Audit(
        Guid actorId, string action, Guid entityId, string? before, string? after,
        string correlationId, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        ActorId = actorId,
        ActorType = "USER",
        Action = action,
        EntityType = "Carrier",
        EntityId = entityId,
        BeforeData = before,
        AfterData = after,
        CorrelationId = correlationId,
        OccurredAt = now,
        CreatedAt = now,
        UpdatedAt = now
    };
}
