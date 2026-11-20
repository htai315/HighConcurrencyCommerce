using System.Text.Json;
using HighConcurrencyCommerce.Application.Categories;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HighConcurrencyCommerce.Infrastructure.Categories;

public sealed class PostgresCategoryManagementService(StoreDbContext db, TimeProvider timeProvider) : ICategoryManagementService
{
    public async Task<IReadOnlyList<AdminCategoryDto>> GetCategoriesAsync(CancellationToken ct = default) =>
        await db.Categories.AsNoTracking()
            .OrderByDescending(category => category.IsVisible)
            .ThenBy(category => category.Name)
            .ThenBy(category => category.Id)
            .Select(category => new AdminCategoryDto(
                category.Id, category.Name, category.Slug, category.IsVisible,
                category.Products.Count, category.CreatedAt, category.UpdatedAt))
            .ToListAsync(ct);

    public async Task<AdminCategoryDto> CreateAsync(
        Guid adminId, CreateCategoryRequest request, string correlationId, CancellationToken ct = default)
    {
        var name = NormalizeName(request.Name);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var category = new Category
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = NormalizeSlug(request.Slug),
            IsVisible = request.IsVisible,
            CreatedAt = now,
            UpdatedAt = now
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Categories.Add(category);
        db.AuditLogs.Add(Audit(adminId, "CATEGORY_CREATED", category.Id, null, Snapshot(category), correlationId, now));
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw new CategoryManagementFailure("CATEGORY_SLUG_CONFLICT", "Slug danh mục đã tồn tại.", 409);
        }

        return ToDto(category, 0);
    }

    public async Task<AdminCategoryDto> UpdateAsync(
        Guid adminId, Guid categoryId, UpdateCategoryRequest request, string correlationId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var category = await db.Categories
            .FromSqlInterpolated($"SELECT * FROM store.categories WHERE id = {categoryId} FOR UPDATE")
            .SingleOrDefaultAsync(ct)
            ?? throw new CategoryManagementFailure("CATEGORY_NOT_FOUND", "Không tìm thấy danh mục.", 404);

        var before = Snapshot(category);
        category.Name = NormalizeName(request.Name);
        category.Slug = NormalizeSlug(request.Slug);
        category.IsVisible = request.IsVisible;
        category.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        db.AuditLogs.Add(Audit(adminId, "CATEGORY_UPDATED", category.Id, before, Snapshot(category), correlationId, category.UpdatedAt));

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw new CategoryManagementFailure("CATEGORY_SLUG_CONFLICT", "Slug danh mục đã tồn tại.", 409);
        }

        var productCount = await db.Products.CountAsync(product => product.CategoryId == category.Id, ct);
        return ToDto(category, productCount);
    }

    public async Task DeleteAsync(Guid adminId, Guid categoryId, string correlationId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var category = await db.Categories
            .FromSqlInterpolated($"SELECT * FROM store.categories WHERE id = {categoryId} FOR UPDATE")
            .SingleOrDefaultAsync(ct)
            ?? throw new CategoryManagementFailure("CATEGORY_NOT_FOUND", "Không tìm thấy danh mục.", 404);

        if (await db.Products.AnyAsync(product => product.CategoryId == categoryId, ct))
            throw new CategoryManagementFailure("CATEGORY_IN_USE", "Danh mục đang được sản phẩm sử dụng; hãy ẩn thay vì xóa.", 409);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        db.Categories.Remove(category);
        db.AuditLogs.Add(Audit(adminId, "CATEGORY_DELETED", category.Id, Snapshot(category), null, correlationId, now));
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (IsForeignKeyViolation(ex))
        {
            throw new CategoryManagementFailure("CATEGORY_IN_USE", "Danh mục vừa được sản phẩm sử dụng và không thể xóa.", 409);
        }
    }

    private static string NormalizeSlug(string slug) => slug.Trim().ToLowerInvariant();
    private static string NormalizeName(string name)
    {
        var normalized = name.Trim();
        if (normalized.Length < 2)
            throw new CategoryManagementFailure("VALIDATION_ERROR", "Tên danh mục phải có ít nhất 2 ký tự có nghĩa.", 400);
        return normalized;
    }
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    private static bool IsForeignKeyViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation };
    private static string Snapshot(Category category) => JsonSerializer.Serialize(new
    {
        category.Id, category.Name, category.Slug, category.IsVisible
    });
    private static AdminCategoryDto ToDto(Category category, int productCount) =>
        new(category.Id, category.Name, category.Slug, category.IsVisible, productCount, category.CreatedAt, category.UpdatedAt);
    private static AuditLog Audit(Guid actorId, string action, Guid entityId, string? before, string? after, string correlationId, DateTime now) => new()
    {
        Id = Guid.NewGuid(), ActorId = actorId, ActorType = "USER", Action = action,
        EntityType = "Category", EntityId = entityId, BeforeData = before, AfterData = after,
        CorrelationId = correlationId, OccurredAt = now, CreatedAt = now, UpdatedAt = now
    };
}
