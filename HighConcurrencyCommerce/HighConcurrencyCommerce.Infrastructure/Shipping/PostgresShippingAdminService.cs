using System.Text.Json;
using HighConcurrencyCommerce.Application.Shipping;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HighConcurrencyCommerce.Infrastructure.Shipping;

public sealed class PostgresShippingAdminService(
    StoreDbContext db,
    IProvinceKeyNormalizer provinceKeyNormalizer,
    TimeProvider timeProvider) : IShippingAdminService
{
    public async Task<IReadOnlyList<AdminShippingMethodSummaryDto>> GetMethodsAsync(CancellationToken ct = default) =>
        await db.ShippingMethods.AsNoTracking()
            .OrderByDescending(x => x.IsDefault)
            .ThenBy(x => x.DisplayOrder)
            .ThenBy(x => x.Code)
            .ThenBy(x => x.Id)
            .Select(x => new AdminShippingMethodSummaryDto(
                x.Id, x.Code, x.Name, x.MethodStatus, x.BaseFee, x.FreeShippingThreshold,
                x.EstimatedMinDays, x.EstimatedMaxDays, x.IsDefault, x.DisplayOrder, x.Revision,
                x.ProvinceRates.Count, x.UpdatedAt))
            .ToListAsync(ct);

    public async Task<AdminShippingMethodDetailDto> GetMethodAsync(Guid methodId, CancellationToken ct = default) =>
        ToDetail(await LoadDetailAsync(methodId, ct));

    public async Task<AdminShippingMethodDetailDto> CreateAsync(
        Guid adminId, CreateShippingMethodRequest request, string correlationId, CancellationToken ct = default)
    {
        ValidateConfiguration(request.Name, request.BaseFee, request.FreeShippingThreshold,
            request.EstimatedMinDays, request.EstimatedMaxDays);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var method = new ShippingMethod
        {
            Id = Guid.NewGuid(),
            Code = request.Code.Trim().ToUpperInvariant(),
            Name = request.Name.Trim(),
            Description = NormalizeOptional(request.Description),
            MethodStatus = "INACTIVE",
            BaseFee = request.BaseFee,
            FreeShippingThreshold = request.FreeShippingThreshold,
            EstimatedMinDays = request.EstimatedMinDays,
            EstimatedMaxDays = request.EstimatedMaxDays,
            IsDefault = false,
            DisplayOrder = request.DisplayOrder,
            Revision = 1,
            CreatedBy = adminId,
            UpdatedBy = adminId,
            CreatedAt = now,
            UpdatedAt = now
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.ShippingMethods.Add(method);
        db.AuditLogs.Add(Audit(adminId, "SHIPPING_METHOD_CREATED", method.Id, null, Snapshot(method), correlationId, now));
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw new ShippingFailure("SHIPPING_METHOD_CODE_CONFLICT", "Mã phương thức giao hàng đã tồn tại.", 409);
        }
        return ToDetail(method);
    }

    public async Task<AdminShippingMethodDetailDto> UpdateAsync(
        Guid adminId, Guid methodId, UpdateShippingMethodRequest request, string correlationId, CancellationToken ct = default)
    {
        ValidateConfiguration(request.Name, request.BaseFee, request.FreeShippingThreshold,
            request.EstimatedMinDays, request.EstimatedMaxDays);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var method = await LockMethodAsync(methodId, ct);
        EnsureRevision(method, request.ExpectedRevision);
        var before = Snapshot(method);
        method.Name = request.Name.Trim();
        method.Description = NormalizeOptional(request.Description);
        method.BaseFee = request.BaseFee;
        method.FreeShippingThreshold = request.FreeShippingThreshold;
        method.EstimatedMinDays = request.EstimatedMinDays;
        method.EstimatedMaxDays = request.EstimatedMaxDays;
        method.DisplayOrder = request.DisplayOrder;
        Touch(method, adminId);
        db.AuditLogs.Add(Audit(adminId, "SHIPPING_METHOD_UPDATED", method.Id, before, Snapshot(method), correlationId, method.UpdatedAt));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetMethodAsync(method.Id, ct);
    }

    public Task<AdminShippingMethodDetailDto> ActivateAsync(
        Guid adminId, Guid methodId, long expectedRevision, string correlationId, CancellationToken ct = default) =>
        ChangeStatusAsync(adminId, methodId, expectedRevision, "ACTIVE", "SHIPPING_METHOD_ACTIVATED", correlationId, ct);

    public async Task<AdminShippingMethodDetailDto> DeactivateAsync(
        Guid adminId, Guid methodId, long expectedRevision, string correlationId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAllMethodsAsync(ct);
        var method = await db.ShippingMethods.SingleOrDefaultAsync(x => x.Id == methodId, ct)
            ?? throw NotFound();
        EnsureRevision(method, expectedRevision);
        if (method.IsDefault)
            throw new ShippingFailure("SHIPPING_DEFAULT_REQUIRED",
                "Hãy đặt một phương thức đang hoạt động khác làm mặc định trước khi ngừng phương thức này.", 409);
        if (method.MethodStatus == "INACTIVE")
        {
            await tx.CommitAsync(ct);
            return await GetMethodAsync(method.Id, ct);
        }
        var before = Snapshot(method);
        method.MethodStatus = "INACTIVE";
        Touch(method, adminId);
        db.AuditLogs.Add(Audit(adminId, "SHIPPING_METHOD_DEACTIVATED", method.Id, before, Snapshot(method), correlationId, method.UpdatedAt));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetMethodAsync(method.Id, ct);
    }

    public async Task<AdminShippingMethodDetailDto> MakeDefaultAsync(
        Guid adminId, Guid methodId, long expectedRevision, string correlationId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAllMethodsAsync(ct);
        var methods = await db.ShippingMethods.OrderBy(x => x.Id).ToListAsync(ct);
        var target = methods.SingleOrDefault(x => x.Id == methodId) ?? throw NotFound();
        EnsureRevision(target, expectedRevision);
        if (target.MethodStatus != "ACTIVE")
            throw new ShippingFailure("SHIPPING_METHOD_NOT_ACTIVE", "Chỉ phương thức đang hoạt động mới có thể làm mặc định.", 409);
        if (target.IsDefault)
        {
            await tx.CommitAsync(ct);
            return await GetMethodAsync(target.Id, ct);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var previousDefaults = methods.Where(x => x.IsDefault).ToList();
        foreach (var current in previousDefaults)
        {
            var before = Snapshot(current);
            current.IsDefault = false;
            current.Revision++;
            current.UpdatedBy = adminId;
            current.UpdatedAt = now;
            db.AuditLogs.Add(Audit(adminId, "SHIPPING_METHOD_DEFAULT_CHANGED", current.Id,
                before, Snapshot(current), correlationId, now));
        }
        // The partial unique index is immediate. Persist clearing the old default first,
        // while staying inside the same transaction, so UUID update ordering cannot violate it.
        if (previousDefaults.Count > 0)
            await db.SaveChangesAsync(ct);

        var targetBefore = Snapshot(target);
        target.IsDefault = true;
        target.Revision++;
        target.UpdatedBy = adminId;
        target.UpdatedAt = now;
        db.AuditLogs.Add(Audit(adminId, "SHIPPING_METHOD_DEFAULT_CHANGED", target.Id,
            targetBefore, Snapshot(target), correlationId, now));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetMethodAsync(target.Id, ct);
    }

    public async Task<AdminShippingMethodDetailDto> UpsertProvinceRateAsync(
        Guid adminId, Guid methodId, string province, UpsertShippingProvinceRateRequest request,
        string correlationId, CancellationToken ct = default)
    {
        EnsureMoney(request.SurchargeFee, nameof(request.SurchargeFee));
        var provinceKey = NormalizeProvince(province);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var method = await LockMethodAsync(methodId, ct);
        EnsureRevision(method, request.ExpectedRevision);
        var rate = await db.ShippingMethodProvinceRates
            .SingleOrDefaultAsync(x => x.ShippingMethodId == methodId && x.ProvinceKey == provinceKey, ct);
        var before = rate is null ? null : RateSnapshot(rate);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (rate is null)
        {
            rate = new ShippingMethodProvinceRate
            {
                Id = Guid.NewGuid(), ShippingMethodId = methodId, ProvinceKey = provinceKey,
                SurchargeFee = request.SurchargeFee, IsActive = request.IsActive,
                CreatedBy = adminId, UpdatedBy = adminId, CreatedAt = now, UpdatedAt = now
            };
            db.ShippingMethodProvinceRates.Add(rate);
        }
        else
        {
            rate.SurchargeFee = request.SurchargeFee;
            rate.IsActive = request.IsActive;
            rate.UpdatedBy = adminId;
            rate.UpdatedAt = now;
        }
        Touch(method, adminId);
        db.AuditLogs.Add(Audit(adminId, "SHIPPING_PROVINCE_RATE_UPSERTED", method.Id,
            before, RateSnapshot(rate), correlationId, now));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetMethodAsync(method.Id, ct);
    }

    public async Task<AdminShippingMethodDetailDto> RemoveProvinceRateAsync(
        Guid adminId, Guid methodId, string province, long expectedRevision,
        string correlationId, CancellationToken ct = default)
    {
        var provinceKey = NormalizeProvince(province);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var method = await LockMethodAsync(methodId, ct);
        EnsureRevision(method, expectedRevision);
        var rate = await db.ShippingMethodProvinceRates
            .SingleOrDefaultAsync(x => x.ShippingMethodId == methodId && x.ProvinceKey == provinceKey, ct)
            ?? throw new ShippingFailure("SHIPPING_PROVINCE_RATE_NOT_FOUND", "Không tìm thấy phụ phí tỉnh/thành.", 404);
        var before = RateSnapshot(rate);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        db.ShippingMethodProvinceRates.Remove(rate);
        Touch(method, adminId);
        db.AuditLogs.Add(Audit(adminId, "SHIPPING_PROVINCE_RATE_REMOVED", method.Id,
            before, null, correlationId, now));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetMethodAsync(method.Id, ct);
    }

    private async Task<AdminShippingMethodDetailDto> ChangeStatusAsync(
        Guid adminId, Guid methodId, long expectedRevision, string status, string action,
        string correlationId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var method = await LockMethodAsync(methodId, ct);
        EnsureRevision(method, expectedRevision);
        if (method.MethodStatus == status)
        {
            await tx.CommitAsync(ct);
            return await GetMethodAsync(method.Id, ct);
        }
        var before = Snapshot(method);
        method.MethodStatus = status;
        Touch(method, adminId);
        db.AuditLogs.Add(Audit(adminId, action, method.Id, before, Snapshot(method), correlationId, method.UpdatedAt));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetMethodAsync(method.Id, ct);
    }

    private async Task<ShippingMethod> LockMethodAsync(Guid methodId, CancellationToken ct) =>
        await db.ShippingMethods
            .FromSqlInterpolated($"SELECT * FROM store.shipping_methods WHERE id = {methodId} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw NotFound();

    private async Task LockAllMethodsAsync(CancellationToken ct) =>
        _ = await db.Database.SqlQuery<Guid>(
            $"SELECT id AS \"Value\" FROM store.shipping_methods ORDER BY id FOR UPDATE").ToListAsync(ct);

    private async Task<ShippingMethod> LoadDetailAsync(Guid methodId, CancellationToken ct) =>
        await db.ShippingMethods.AsNoTracking().Include(x => x.ProvinceRates)
            .SingleOrDefaultAsync(x => x.Id == methodId, ct) ?? throw NotFound();

    private static AdminShippingMethodDetailDto ToDetail(ShippingMethod method) => new(
        method.Id, method.Code, method.Name, method.Description, method.MethodStatus,
        method.BaseFee, method.FreeShippingThreshold, method.EstimatedMinDays, method.EstimatedMaxDays,
        method.IsDefault, method.DisplayOrder, method.Revision, method.CreatedAt, method.UpdatedAt,
        method.ProvinceRates.OrderBy(x => x.ProvinceKey).ThenBy(x => x.Id)
            .Select(x => new AdminShippingProvinceRateDto(x.Id, x.ProvinceKey, x.SurchargeFee, x.IsActive, x.UpdatedAt))
            .ToList());

    private void Touch(ShippingMethod method, Guid adminId)
    {
        method.Revision++;
        method.UpdatedBy = adminId;
        method.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
    }

    private static void EnsureRevision(ShippingMethod method, long expectedRevision)
    {
        if (method.Revision != expectedRevision)
            throw new ShippingFailure("SHIPPING_METHOD_STALE",
                $"Phương thức đã thay đổi (revision hiện tại: {method.Revision}). Hãy tải lại dữ liệu.", 409);
    }

    private static void ValidateConfiguration(string name, decimal baseFee, decimal? threshold, short minDays, short maxDays)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length < 2)
            throw new ShippingFailure("VALIDATION_ERROR", "Tên phương thức phải có ít nhất 2 ký tự.");
        EnsureMoney(baseFee, nameof(baseFee));
        if (threshold is not null && threshold <= 0)
            throw new ShippingFailure("VALIDATION_ERROR", "Ngưỡng miễn phí phải lớn hơn 0.");
        if (threshold is not null) EnsureMoney(threshold.Value, nameof(threshold));
        if (minDays < 0 || maxDays < minDays || maxDays > 365)
            throw new ShippingFailure("VALIDATION_ERROR", "Khoảng thời gian giao hàng không hợp lệ.");
    }

    private static void EnsureMoney(decimal value, string field)
    {
        if (value < 0 || value != decimal.Floor(value))
            throw new ShippingFailure("VALIDATION_ERROR", $"{field} phải là số VND nguyên không âm.");
    }

    private string NormalizeProvince(string province)
    {
        try { return provinceKeyNormalizer.Normalize(Uri.UnescapeDataString(province)); }
        catch (ArgumentException ex) { throw new ShippingFailure("VALIDATION_ERROR", ex.Message); }
    }

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static ShippingFailure NotFound() => new("SHIPPING_METHOD_NOT_FOUND", "Không tìm thấy phương thức giao hàng.", 404);
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    private static string Snapshot(ShippingMethod method) => JsonSerializer.Serialize(new
    {
        method.Id, method.Code, method.Name, method.Description, method.MethodStatus, method.BaseFee,
        method.FreeShippingThreshold, method.EstimatedMinDays, method.EstimatedMaxDays,
        method.IsDefault, method.DisplayOrder, method.Revision
    });
    private static string RateSnapshot(ShippingMethodProvinceRate rate) => JsonSerializer.Serialize(new
    {
        rate.Id, rate.ShippingMethodId, rate.ProvinceKey, rate.SurchargeFee, rate.IsActive
    });
    private static AuditLog Audit(Guid actorId, string action, Guid entityId, string? before, string? after,
        string correlationId, DateTime now) => new()
    {
        Id = Guid.NewGuid(), ActorId = actorId, ActorType = "USER", Action = action,
        EntityType = "ShippingMethod", EntityId = entityId, BeforeData = before, AfterData = after,
        CorrelationId = correlationId, OccurredAt = now, CreatedAt = now, UpdatedAt = now
    };
}
