using System.Data;
using System.Text.Json;
using HighConcurrencyCommerce.Application.ProductManagement;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HighConcurrencyCommerce.Infrastructure.ProductManagement;

public sealed class PostgresProductManagementService(StoreDbContext db) : IProductManagementService
{
    private static ProductDraftDto ToDraftDto(Product p) =>
        new(
            p.Id,
            p.Title,
            p.Description,
            p.CategoryId,
            p.PublicationStatus,
            p.CreatedAt,
            p.PublishedAt,
            p.ProductSkus
                .OrderBy(s => s.SkuCode)
                .Select(s => new ProductSkuDto(s.Id, s.ProductId, s.SkuCode, s.OptionLabel, s.RegularPrice, s.SkuStatus))
                .ToList(),
            p.ProductImages
                .OrderBy(img => img.DisplayOrder)
                .ThenBy(img => img.CreatedAt)
                .Select(img => new ProductImageItemDto(img.Id, img.ProductId, img.FileKey, img.AltText, img.DisplayOrder, img.ImageStatus))
                .ToList());

    public async Task<IReadOnlyList<ProductDraftDto>> GetAllProductsAsync(CancellationToken ct = default)
    {
        var products = await db.Products
            .AsNoTracking()
            .Include(p => p.ProductSkus)
            .Include(p => p.ProductImages)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);

        return products.Select(ToDraftDto).ToList();
    }

    public async Task<ProductDraftDto> GetProductAsync(Guid productId, CancellationToken ct = default)
    {
        var product = await db.Products
            .AsNoTracking()
            .Include(p => p.ProductSkus)
            .Include(p => p.ProductImages)
            .FirstOrDefaultAsync(p => p.Id == productId, ct);

        if (product is null)
        {
            throw new ProductManagementFailure("NOT_FOUND", "Không tìm thấy sản phẩm.");
        }

        return ToDraftDto(product);
    }

    public async Task<ProductDraftDto> CreateDraftAsync(Guid staffId, CreateProductDraftRequest request, CancellationToken ct = default)
    {
        if (request.CategoryId.HasValue)
        {
            var categoryExists = await db.Categories.AnyAsync(c => c.Id == request.CategoryId.Value, ct);
            if (!categoryExists) throw new ProductManagementFailure("NOT_FOUND", "Danh mục không tồn tại.");
        }

        var product = new Product
        {
            Id = Guid.NewGuid(),
            CreatedBy = staffId,
            Title = request.Title.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            CategoryId = request.CategoryId,
            PublicationStatus = "DRAFT",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Products.Add(product);
        await db.SaveChangesAsync(ct);

        return ToDraftDto(product);
    }

    public async Task<ProductDraftDto> UpdateDraftAsync(Guid staffId, Guid productId, UpdateProductDraftRequest request, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var product = await db.Products
            .FromSqlInterpolated($"SELECT * FROM store.products WHERE id = {productId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);

        if (product is null)
        {
            throw new ProductManagementFailure("NOT_FOUND", "Không tìm thấy sản phẩm.");
        }

        if (product.PublicationStatus != "DRAFT")
        {
            throw new ProductManagementFailure("INVALID_STATE", "Chỉ có thể sửa sản phẩm ở trạng thái DRAFT.");
        }

        if (request.CategoryId.HasValue)
        {
            var categoryExists = await db.Categories.AnyAsync(c => c.Id == request.CategoryId.Value, ct);
            if (!categoryExists) throw new ProductManagementFailure("NOT_FOUND", "Danh mục không tồn tại.");
        }

        product.Title = request.Title.Trim();
        product.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        product.CategoryId = request.CategoryId;
        product.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        await db.Entry(product).Collection(p => p.ProductSkus).LoadAsync(ct);
        await db.Entry(product).Collection(p => p.ProductImages).LoadAsync(ct);

        await tx.CommitAsync(ct);
        return ToDraftDto(product);
    }

    public async Task<ProductSkuDto> AddSkuAsync(Guid staffId, Guid productId, CreateSkuRequest request, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var product = await db.Products
            .FromSqlInterpolated($"SELECT * FROM store.products WHERE id = {productId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);

        if (product is null)
        {
            throw new ProductManagementFailure("NOT_FOUND", "Không tìm thấy sản phẩm.");
        }

        if (product.PublicationStatus != "DRAFT")
        {
            throw new ProductManagementFailure("INVALID_STATE", "Chỉ có thể thêm biến thể cho sản phẩm ở trạng thái DRAFT.");
        }

        var skuCode = request.SkuCode.Trim();
        var exists = await db.ProductSkus.AnyAsync(s => s.SkuCode == skuCode, ct);
        if (exists)
        {
            throw new ProductManagementFailure("CONFLICT", "Mã SKU này đã tồn tại trong hệ thống.");
        }

        var sku = new ProductSku
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            SkuCode = skuCode,
            OptionLabel = request.OptionLabel.Trim(),
            RegularPrice = 0,
            SkuStatus = "ACTIVE",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.ProductSkus.Add(sku);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ProductManagementFailure("CONFLICT", "Mã SKU này đã tồn tại trong hệ thống.");
        }

        await tx.CommitAsync(ct);
        return new ProductSkuDto(sku.Id, sku.ProductId, sku.SkuCode, sku.OptionLabel, sku.RegularPrice, sku.SkuStatus);
    }

    public async Task<ProductImageItemDto> AddImageAsync(Guid staffId, Guid productId, AddProductImageRequest request, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var product = await db.Products
            .FromSqlInterpolated($"SELECT * FROM store.products WHERE id = {productId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);

        if (product is null)
        {
            throw new ProductManagementFailure("NOT_FOUND", "Không tìm thấy sản phẩm.");
        }

        if (product.PublicationStatus != "DRAFT")
        {
            throw new ProductManagementFailure("INVALID_STATE", "Chỉ có thể thêm ảnh cho sản phẩm ở trạng thái DRAFT.");
        }

        var image = new ProductImage
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            FileKey = request.FileKey.Trim(),
            AltText = string.IsNullOrWhiteSpace(request.AltText) ? null : request.AltText.Trim(),
            DisplayOrder = request.DisplayOrder,
            ImageStatus = "ACTIVE",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.ProductImages.Add(image);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new ProductImageItemDto(image.Id, image.ProductId, image.FileKey, image.AltText, image.DisplayOrder, image.ImageStatus);
    }

    public async Task DeleteImageAsync(Guid staffId, Guid productId, Guid imageId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var product = await db.Products
            .FromSqlInterpolated($"SELECT * FROM store.products WHERE id = {productId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);

        if (product is null)
        {
            throw new ProductManagementFailure("NOT_FOUND", "Không tìm thấy sản phẩm.");
        }

        if (product.PublicationStatus != "DRAFT")
        {
            throw new ProductManagementFailure("INVALID_STATE", "Chỉ có thể xóa ảnh của sản phẩm ở trạng thái DRAFT.");
        }

        var image = await db.ProductImages.FirstOrDefaultAsync(img => img.Id == imageId && img.ProductId == productId, ct);
        if (image is null)
        {
            throw new ProductManagementFailure("NOT_FOUND", "Không tìm thấy ảnh.");
        }

        db.ProductImages.Remove(image);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<ProductSkuDto> UpdateSkuPriceAsync(Guid adminId, Guid skuId, UpdateSkuPriceRequest request, string correlationId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var sku = await db.ProductSkus
            .FromSqlInterpolated($"SELECT * FROM store.product_skus WHERE id = {skuId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);
        if (sku is null)
        {
            throw new ProductManagementFailure("NOT_FOUND", "Không tìm thấy biến thể SKU.");
        }

        var oldPrice = sku.RegularPrice;
        var newPrice = request.Price;
        sku.RegularPrice = newPrice;
        sku.UpdatedAt = DateTime.UtcNow;

        var audit = new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = adminId,
            ActorType = "USER",
            Action = "SKU_PRICE_UPDATED",
            EntityType = "ProductSku",
            EntityId = skuId,
            BeforeData = JsonSerializer.Serialize(new { regularPrice = oldPrice }),
            AfterData = JsonSerializer.Serialize(new { regularPrice = newPrice }),
            CorrelationId = correlationId,
            OccurredAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.AuditLogs.Add(audit);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new ProductSkuDto(sku.Id, sku.ProductId, sku.SkuCode, sku.OptionLabel, sku.RegularPrice, sku.SkuStatus);
    }

    public async Task<PublishProductResult> PublishProductAsync(Guid adminId, Guid productId, string correlationId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var product = await db.Products
            .FromSqlInterpolated($"SELECT * FROM store.products WHERE id = {productId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);

        if (product is null)
        {
            throw new ProductManagementFailure("NOT_FOUND", "Không tìm thấy sản phẩm.");
        }

        if (product.PublicationStatus != "DRAFT")
        {
            throw new ProductManagementFailure("CANNOT_PUBLISH", "Chỉ có thể Publish sản phẩm ở trạng thái DRAFT.");
        }

        await db.Entry(product).Collection(p => p.ProductSkus).LoadAsync(ct);

        var activeSkus = product.ProductSkus.Where(s => s.SkuStatus == "ACTIVE").ToList();
        if (activeSkus.Count == 0)
        {
            throw new ProductManagementFailure("CANNOT_PUBLISH", "Sản phẩm phải có ít nhất một SKU ở trạng thái ACTIVE để Publish.");
        }

        if (activeSkus.Any(s => s.RegularPrice <= 0))
        {
            throw new ProductManagementFailure("CANNOT_PUBLISH", "Tất cả các SKU ACTIVE phải được Admin định giá lớn hơn 0 trước khi Publish.");
        }

        var now = DateTime.UtcNow;
        product.PublicationStatus = "PUBLISHED";
        product.PublishedBy = adminId;
        product.PublishedAt = now;
        product.UpdatedAt = now;

        var audit = new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = adminId,
            ActorType = "USER",
            Action = "PRODUCT_PUBLISHED",
            EntityType = "Product",
            EntityId = productId,
            BeforeData = JsonSerializer.Serialize(new { publicationStatus = "DRAFT" }),
            AfterData = JsonSerializer.Serialize(new { publicationStatus = "PUBLISHED", publishedAt = now }),
            CorrelationId = correlationId,
            OccurredAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.AuditLogs.Add(audit);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new PublishProductResult(product.Id, product.PublicationStatus, now);
    }
}

