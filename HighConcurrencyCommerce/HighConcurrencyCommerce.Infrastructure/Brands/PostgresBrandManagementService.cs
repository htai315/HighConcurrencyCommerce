using System.Text.Json;
using HighConcurrencyCommerce.Application.Brands;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HighConcurrencyCommerce.Infrastructure.Brands;

public sealed class PostgresBrandManagementService(StoreDbContext db, TimeProvider timeProvider) : IBrandManagementService
{
    public async Task<IReadOnlyList<BrandDto>> GetAllAsync(CancellationToken ct = default) =>
        await db.Brands.AsNoTracking().OrderByDescending(x => x.IsActive).ThenBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => new BrandDto(x.Id, x.Name, x.Slug, x.Country, x.WebsiteUrl, x.IsActive, x.Products.Count, x.CreatedAt, x.UpdatedAt)).ToListAsync(ct);

    public async Task<BrandDto> CreateAsync(Guid adminId, BrandMutationRequest request, string correlationId, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var brand = new Brand { Id = Guid.NewGuid(), Name = Name(request.Name), Slug = request.Slug.Trim().ToLowerInvariant(), Country = Empty(request.Country), WebsiteUrl = Empty(request.WebsiteUrl), IsActive = request.IsActive, CreatedAt = now, UpdatedAt = now };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Brands.Add(brand); db.AuditLogs.Add(Audit(adminId, "BRAND_CREATED", brand.Id, null, Snapshot(brand), correlationId, now));
        await Save(tx, "BRAND_SLUG_CONFLICT", "Slug thương hiệu đã tồn tại.", ct);
        return Dto(brand, 0);
    }

    public async Task<BrandDto> UpdateAsync(Guid adminId, Guid id, BrandMutationRequest request, string correlationId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var brand = await db.Brands.FromSqlInterpolated($"SELECT * FROM store.brands WHERE id={id} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw new BrandManagementFailure("BRAND_NOT_FOUND", "Không tìm thấy thương hiệu.", 404);
        var before = Snapshot(brand); var now = timeProvider.GetUtcNow().UtcDateTime;
        brand.Name = Name(request.Name); brand.Slug = request.Slug.Trim().ToLowerInvariant(); brand.Country = Empty(request.Country); brand.WebsiteUrl = Empty(request.WebsiteUrl); brand.IsActive = request.IsActive; brand.UpdatedAt = now;
        db.AuditLogs.Add(Audit(adminId, "BRAND_UPDATED", brand.Id, before, Snapshot(brand), correlationId, now));
        await Save(tx, "BRAND_SLUG_CONFLICT", "Slug thương hiệu đã tồn tại.", ct);
        return Dto(brand, await db.Products.CountAsync(x => x.BrandId == id, ct));
    }

    public async Task DeleteAsync(Guid adminId, Guid id, string correlationId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var brand = await db.Brands.FromSqlInterpolated($"SELECT * FROM store.brands WHERE id={id} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw new BrandManagementFailure("BRAND_NOT_FOUND", "Không tìm thấy thương hiệu.", 404);
        if (await db.Products.AnyAsync(x => x.BrandId == id, ct)) throw new BrandManagementFailure("BRAND_IN_USE", "Thương hiệu đang được sản phẩm sử dụng; hãy tạm ẩn thay vì xóa.", 409);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        db.Brands.Remove(brand); db.AuditLogs.Add(Audit(adminId, "BRAND_DELETED", brand.Id, Snapshot(brand), null, correlationId, now));
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation }) { throw new BrandManagementFailure("BRAND_IN_USE", "Thương hiệu vừa được sản phẩm sử dụng và không thể xóa.", 409); }
    }

    private async Task Save(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx, string code, string message, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) { throw new BrandManagementFailure(code, message, 409); }
    }
    private static string Name(string value) { var result = value.Trim(); if (result.Length < 2) throw new BrandManagementFailure("VALIDATION_ERROR", "Tên thương hiệu phải có ít nhất 2 ký tự có nghĩa.", 400); return result; }
    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Snapshot(Brand x) => JsonSerializer.Serialize(new { x.Id, x.Name, x.Slug, x.Country, x.WebsiteUrl, x.IsActive });
    private static BrandDto Dto(Brand x, int count) => new(x.Id, x.Name, x.Slug, x.Country, x.WebsiteUrl, x.IsActive, count, x.CreatedAt, x.UpdatedAt);
    private static AuditLog Audit(Guid actor, string action, Guid entity, string? before, string? after, string correlation, DateTime now) => new() { Id = Guid.NewGuid(), ActorId = actor, ActorType = "USER", Action = action, EntityType = "Brand", EntityId = entity, BeforeData = before, AfterData = after, CorrelationId = correlation, OccurredAt = now, CreatedAt = now, UpdatedAt = now };
}
