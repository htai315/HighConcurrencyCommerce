using System.Text.Json;
using System.Text.RegularExpressions;
using HighConcurrencyCommerce.Application.Vouchers;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace HighConcurrencyCommerce.Infrastructure.Vouchers;

public sealed partial class PostgresVoucherAdminService(
    StoreDbContext db,
    TimeProvider timeProvider) : IVoucherAdminService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[A-Z0-9_-]{3,32}$")]
    private static partial Regex CodeRegex();

    private static string NormalizeCode(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new VoucherFailure("VALIDATION_ERROR", "Mã voucher không được để trống.", 400);

        var normalized = raw.Trim().ToUpperInvariant();
        if (!CodeRegex().IsMatch(normalized))
            throw new VoucherFailure("VALIDATION_ERROR", "Mã voucher từ 3 đến 32 ký tự, chỉ gồm chữ hoa, số, gạch ngang và gạch dưới.", 400);

        return normalized;
    }

    private static void ValidateEconomics(
        string discountType,
        decimal discountValue,
        decimal? maxDiscountAmount,
        decimal minimumOrderAmount,
        int totalUsageLimit,
        int perCustomerLimit,
        decimal budgetTotal,
        DateTime startsAt,
        DateTime endsAt)
    {
        if (discountType is not ("FIXED_AMOUNT" or "PERCENTAGE"))
            throw new VoucherFailure("VALIDATION_ERROR", "Loại chiết khấu chỉ nhận FIXED_AMOUNT hoặc PERCENTAGE.", 400);

        if (startsAt >= endsAt)
            throw new VoucherFailure("VALIDATION_ERROR", "Thời gian bắt đầu phải trước thời gian kết thúc.", 400);

        if (totalUsageLimit <= 0)
            throw new VoucherFailure("VALIDATION_ERROR", "Tổng lượt sử dụng phải lớn hơn 0.", 400);

        if (perCustomerLimit <= 0 || perCustomerLimit > totalUsageLimit)
            throw new VoucherFailure("VALIDATION_ERROR", "Lượt dùng mỗi khách phải từ 1 và không vượt quá tổng lượt dùng.", 400);

        if (minimumOrderAmount < 0 || minimumOrderAmount != Math.Floor(minimumOrderAmount))
            throw new VoucherFailure("VALIDATION_ERROR", "Giá trị đơn hàng tối thiểu phải là số nguyên không âm.", 400);

        if (budgetTotal <= 0 || budgetTotal != Math.Floor(budgetTotal))
            throw new VoucherFailure("VALIDATION_ERROR", "Tổng ngân sách phải là số nguyên dương.", 400);

        if (discountType == "FIXED_AMOUNT")
        {
            if (discountValue <= 0 || discountValue != Math.Floor(discountValue))
                throw new VoucherFailure("VALIDATION_ERROR", "Giá trị giảm số tiền cố định phải là số nguyên dương.", 400);

            if (maxDiscountAmount.HasValue)
                throw new VoucherFailure("VALIDATION_ERROR", "Voucher giảm số tiền cố định không được có trần giảm giá tối đa.", 400);

            if (budgetTotal < discountValue)
                throw new VoucherFailure("VALIDATION_ERROR", "Tổng ngân sách không được nhỏ hơn giá trị chiết khấu của voucher.", 400);
        }
        else // PERCENTAGE
        {
            if (discountValue < 1 || discountValue > 99 || discountValue != Math.Floor(discountValue))
                throw new VoucherFailure("VALIDATION_ERROR", "Phần trăm giảm giá phải là số nguyên từ 1 đến 99.", 400);

            if (!maxDiscountAmount.HasValue || maxDiscountAmount.Value <= 0 || maxDiscountAmount.Value != Math.Floor(maxDiscountAmount.Value))
                throw new VoucherFailure("VALIDATION_ERROR", "Voucher phần trăm bắt buộc phải có mức giảm tối đa là số nguyên dương.", 400);

            if (budgetTotal < maxDiscountAmount.Value)
                throw new VoucherFailure("VALIDATION_ERROR", "Tổng ngân sách không được nhỏ hơn mức giảm tối đa của voucher.", 400);
        }
    }

    private static string Snapshot(Voucher v) =>
        JsonSerializer.Serialize(new
        {
            v.Id,
            v.Code,
            v.Name,
            v.VoucherStatus,
            v.DiscountType,
            v.DiscountValue,
            v.MaxDiscountAmount,
            v.MinimumOrderAmount,
            v.TotalUsageLimit,
            v.PerCustomerLimit,
            v.BudgetTotal,
            v.StartsAt,
            v.EndsAt,
            v.Revision
        }, JsonOptions);

    private static AdminVoucherDetailDto ToDetailDto(Voucher v) =>
        new(
            v.Id,
            v.Code,
            v.Name,
            v.VoucherStatus,
            v.DiscountType,
            v.DiscountValue,
            v.MaxDiscountAmount,
            v.MinimumOrderAmount,
            v.TotalUsageLimit,
            v.PerCustomerLimit,
            v.UsageHeld,
            v.UsageConsumed,
            Math.Max(0, v.TotalUsageLimit - (v.UsageHeld + v.UsageConsumed)),
            v.BudgetTotal,
            v.BudgetHeld,
            v.BudgetConsumed,
            Math.Max(0m, v.BudgetTotal - (v.BudgetHeld + v.BudgetConsumed)),
            v.StartsAt,
            v.EndsAt,
            v.ActivatedAt,
            v.CreatedBy,
            v.ActivatedBy,
            v.Revision,
            v.CreatedAt,
            v.UpdatedAt);

    public async Task<AdminVoucherPageDto> GetVouchersAsync(AdminVoucherQuery query, CancellationToken ct = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var q = db.Vouchers.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var status = query.Status.Trim().ToUpperInvariant();
            q = q.Where(v => v.VoucherStatus == status);
        }

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var search = query.Q.Trim();
            q = q.Where(v => EF.Functions.ILike(v.Code, $"%{search}%") || EF.Functions.ILike(v.Name, $"%{search}%"));
        }

        var totalCount = await q.CountAsync(ct);

        long rawOffset = (long)(page - 1) * pageSize;
        if (rawOffset > int.MaxValue || rawOffset >= totalCount)
        {
            return new AdminVoucherPageDto([], totalCount, page, pageSize);
        }

        var items = await q
            .OrderByDescending(v => v.CreatedAt)
            .Skip((int)rawOffset)
            .Take(pageSize)
            .Select(v => new AdminVoucherSummaryDto(
                v.Id,
                v.Code,
                v.Name,
                v.VoucherStatus,
                v.DiscountType,
                v.DiscountValue,
                v.MaxDiscountAmount,
                v.MinimumOrderAmount,
                v.TotalUsageLimit,
                v.PerCustomerLimit,
                v.UsageHeld,
                v.UsageConsumed,
                Math.Max(0, v.TotalUsageLimit - (v.UsageHeld + v.UsageConsumed)),
                v.BudgetTotal,
                v.BudgetHeld,
                v.BudgetConsumed,
                Math.Max(0m, v.BudgetTotal - (v.BudgetHeld + v.BudgetConsumed)),
                v.StartsAt,
                v.EndsAt,
                v.ActivatedAt,
                v.CreatedBy,
                v.ActivatedBy,
                v.CreatedAt,
                v.UpdatedAt))
            .ToListAsync(ct);

        return new AdminVoucherPageDto(items, totalCount, page, pageSize);
    }

    public async Task<AdminVoucherDetailDto> GetVoucherByIdAsync(Guid id, CancellationToken ct = default)
    {
        var voucher = await db.Vouchers.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct)
            ?? throw new VoucherFailure("NOT_FOUND", "Không tìm thấy voucher.", 404);

        return ToDetailDto(voucher);
    }

    public async Task<AdminVoucherDetailDto> CreateVoucherAsync(
        Guid adminId,
        CreateVoucherRequest request,
        string correlationId,
        CancellationToken ct = default)
    {
        var code = NormalizeCode(request.Code);
        ValidateEconomics(
            request.DiscountType,
            request.DiscountValue,
            request.MaxDiscountAmount,
            request.MinimumOrderAmount,
            request.TotalUsageLimit,
            request.PerCustomerLimit,
            request.BudgetTotal,
            request.StartsAt,
            request.EndsAt);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var exists = await db.Vouchers.AnyAsync(v => v.Code == code, ct);
        if (exists)
            throw new VoucherFailure("CONFLICT", "Mã voucher đã tồn tại trên hệ thống.", 409);

        var name = (request.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = code;
        if (name.Length > 100)
            throw new VoucherFailure("VALIDATION_ERROR", "Tên chương trình voucher không được vượt quá 100 ký tự.", 400);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var voucher = new Voucher
        {
            Id = Guid.NewGuid(),
            Code = code,
            Name = name,
            VoucherStatus = "DRAFT",
            DiscountType = request.DiscountType,
            DiscountValue = request.DiscountValue,
            MaxDiscountAmount = request.DiscountType == "PERCENTAGE" ? request.MaxDiscountAmount : null,
            MinimumOrderAmount = request.MinimumOrderAmount,
            TotalUsageLimit = request.TotalUsageLimit,
            PerCustomerLimit = request.PerCustomerLimit,
            UsageHeld = 0,
            UsageConsumed = 0,
            BudgetTotal = request.BudgetTotal,
            BudgetHeld = 0m,
            BudgetConsumed = 0m,
            StartsAt = request.StartsAt,
            EndsAt = request.EndsAt,
            ActivatedAt = null,
            CreatedBy = adminId,
            ActivatedBy = null,
            Revision = 0,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Vouchers.Add(voucher);
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = adminId,
            ActorType = "USER",
            Action = "VOUCHER_CREATED",
            EntityType = "Voucher",
            EntityId = voucher.Id,
            BeforeData = null,
            AfterData = Snapshot(voucher),
            CorrelationId = correlationId,
            OccurredAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            throw new VoucherFailure("CONFLICT", "Mã voucher đã tồn tại trên hệ thống.", 409);
        }

        return ToDetailDto(voucher);
    }

    public async Task<AdminVoucherDetailDto> UpdateVoucherAsync(
        Guid adminId,
        Guid id,
        UpdateVoucherRequest request,
        string correlationId,
        CancellationToken ct = default)
    {
        var code = NormalizeCode(request.Code);
        ValidateEconomics(
            request.DiscountType,
            request.DiscountValue,
            request.MaxDiscountAmount,
            request.MinimumOrderAmount,
            request.TotalUsageLimit,
            request.PerCustomerLimit,
            request.BudgetTotal,
            request.StartsAt,
            request.EndsAt);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var voucher = await db.Vouchers
            .FromSqlInterpolated($"SELECT * FROM store.vouchers WHERE id = {id} FOR UPDATE")
            .FirstOrDefaultAsync(ct)
            ?? throw new VoucherFailure("NOT_FOUND", "Không tìm thấy voucher.", 404);

        if (voucher.VoucherStatus != "DRAFT")
            throw new VoucherFailure("INVALID_STATE", "Chỉ voucher ở trạng thái DRAFT mới được phép chỉnh sửa.", 409);

        if (voucher.Code != code)
        {
            var codeExists = await db.Vouchers.AnyAsync(v => v.Code == code && v.Id != id, ct);
            if (codeExists)
                throw new VoucherFailure("CONFLICT", "Mã voucher mới đã tồn tại trên hệ thống.", 409);
        }

        var name = (request.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = code;
        if (name.Length > 100)
            throw new VoucherFailure("VALIDATION_ERROR", "Tên chương trình voucher không được vượt quá 100 ký tự.", 400);

        var before = Snapshot(voucher);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        voucher.Code = code;
        voucher.Name = name;
        voucher.DiscountType = request.DiscountType;
        voucher.DiscountValue = request.DiscountValue;
        voucher.MaxDiscountAmount = request.DiscountType == "PERCENTAGE" ? request.MaxDiscountAmount : null;
        voucher.MinimumOrderAmount = request.MinimumOrderAmount;
        voucher.TotalUsageLimit = request.TotalUsageLimit;
        voucher.PerCustomerLimit = request.PerCustomerLimit;
        voucher.BudgetTotal = request.BudgetTotal;
        voucher.StartsAt = request.StartsAt;
        voucher.EndsAt = request.EndsAt;
        voucher.Revision++;
        voucher.UpdatedAt = now;

        var after = Snapshot(voucher);
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = adminId,
            ActorType = "USER",
            Action = "VOUCHER_UPDATED",
            EntityType = "Voucher",
            EntityId = voucher.Id,
            BeforeData = before,
            AfterData = after,
            CorrelationId = correlationId,
            OccurredAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            throw new VoucherFailure("CONFLICT", "Mã voucher mới đã tồn tại trên hệ thống.", 409);
        }

        return ToDetailDto(voucher);
    }

    public async Task<AdminVoucherDetailDto> ActivateVoucherAsync(
        Guid adminId,
        Guid id,
        string correlationId,
        CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var voucher = await db.Vouchers
            .FromSqlInterpolated($"SELECT * FROM store.vouchers WHERE id = {id} FOR UPDATE")
            .FirstOrDefaultAsync(ct)
            ?? throw new VoucherFailure("NOT_FOUND", "Không tìm thấy voucher.", 404);

        if (voucher.VoucherStatus != "DRAFT")
            throw new VoucherFailure("INVALID_STATE", "Chỉ voucher ở trạng thái DRAFT mới có thể kích hoạt.", 409);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (voucher.EndsAt <= now)
            throw new VoucherFailure("INVALID_STATE", "Không thể kích hoạt voucher đã quá thời gian kết thúc.", 409);

        ValidateEconomics(
            voucher.DiscountType,
            voucher.DiscountValue,
            voucher.MaxDiscountAmount,
            voucher.MinimumOrderAmount,
            voucher.TotalUsageLimit,
            voucher.PerCustomerLimit,
            voucher.BudgetTotal,
            voucher.StartsAt,
            voucher.EndsAt);

        var before = Snapshot(voucher);

        voucher.VoucherStatus = "ACTIVE";
        voucher.ActivatedAt = now;
        voucher.ActivatedBy = adminId;
        voucher.Revision++;
        voucher.UpdatedAt = now;

        var after = Snapshot(voucher);
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = adminId,
            ActorType = "USER",
            Action = "VOUCHER_ACTIVATED",
            EntityType = "Voucher",
            EntityId = voucher.Id,
            BeforeData = before,
            AfterData = after,
            CorrelationId = correlationId,
            OccurredAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return ToDetailDto(voucher);
    }

    public async Task<AdminVoucherDetailDto> PauseVoucherAsync(
        Guid adminId,
        Guid id,
        string correlationId,
        CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var voucher = await db.Vouchers
            .FromSqlInterpolated($"SELECT * FROM store.vouchers WHERE id = {id} FOR UPDATE")
            .FirstOrDefaultAsync(ct)
            ?? throw new VoucherFailure("NOT_FOUND", "Không tìm thấy voucher.", 404);

        if (voucher.VoucherStatus != "ACTIVE")
            throw new VoucherFailure("INVALID_STATE", "Chỉ voucher đang ở trạng thái ACTIVE mới có thể tạm dừng.", 409);

        var before = Snapshot(voucher);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        voucher.VoucherStatus = "PAUSED";
        voucher.Revision++;
        voucher.UpdatedAt = now;

        var after = Snapshot(voucher);
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = adminId,
            ActorType = "USER",
            Action = "VOUCHER_PAUSED",
            EntityType = "Voucher",
            EntityId = voucher.Id,
            BeforeData = before,
            AfterData = after,
            CorrelationId = correlationId,
            OccurredAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return ToDetailDto(voucher);
    }

    public async Task<AdminVoucherDetailDto> ResumeVoucherAsync(
        Guid adminId,
        Guid id,
        string correlationId,
        CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var voucher = await db.Vouchers
            .FromSqlInterpolated($"SELECT * FROM store.vouchers WHERE id = {id} FOR UPDATE")
            .FirstOrDefaultAsync(ct)
            ?? throw new VoucherFailure("NOT_FOUND", "Không tìm thấy voucher.", 404);

        if (voucher.VoucherStatus != "PAUSED")
            throw new VoucherFailure("INVALID_STATE", "Chỉ voucher đang ở trạng thái PAUSED mới có thể mở lại.", 409);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (voucher.EndsAt <= now)
            throw new VoucherFailure("INVALID_STATE", "Không thể mở lại voucher đã hết hạn.", 409);

        if (voucher.UsageHeld + voucher.UsageConsumed >= voucher.TotalUsageLimit)
            throw new VoucherFailure("INVALID_STATE", "Không thể mở lại voucher đã đạt giới hạn sử dụng.", 409);

        if (voucher.BudgetHeld + voucher.BudgetConsumed >= voucher.BudgetTotal)
            throw new VoucherFailure("INVALID_STATE", "Không thể mở lại voucher đã cạn ngân sách.", 409);

        var before = Snapshot(voucher);

        voucher.VoucherStatus = "ACTIVE";
        voucher.Revision++;
        voucher.UpdatedAt = now;

        var after = Snapshot(voucher);
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = adminId,
            ActorType = "USER",
            Action = "VOUCHER_RESUMED",
            EntityType = "Voucher",
            EntityId = voucher.Id,
            BeforeData = before,
            AfterData = after,
            CorrelationId = correlationId,
            OccurredAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return ToDetailDto(voucher);
    }

    public async Task<AdminVoucherDetailDto> EndVoucherAsync(
        Guid adminId,
        Guid id,
        string correlationId,
        CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var voucher = await db.Vouchers
            .FromSqlInterpolated($"SELECT * FROM store.vouchers WHERE id = {id} FOR UPDATE")
            .FirstOrDefaultAsync(ct)
            ?? throw new VoucherFailure("NOT_FOUND", "Không tìm thấy voucher.", 404);

        if (voucher.VoucherStatus == "ENDED")
            throw new VoucherFailure("INVALID_STATE", "Voucher đã ở trạng thái kết thúc.", 409);

        if (voucher.VoucherStatus is not ("ACTIVE" or "PAUSED"))
            throw new VoucherFailure("INVALID_STATE", "Chỉ voucher đang ACTIVE hoặc PAUSED mới có thể kết thúc.", 409);

        var before = Snapshot(voucher);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        voucher.VoucherStatus = "ENDED";
        voucher.Revision++;
        voucher.UpdatedAt = now;

        var after = Snapshot(voucher);
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = adminId,
            ActorType = "USER",
            Action = "VOUCHER_ENDED",
            EntityType = "Voucher",
            EntityId = voucher.Id,
            BeforeData = before,
            AfterData = after,
            CorrelationId = correlationId,
            OccurredAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return ToDetailDto(voucher);
    }
}

