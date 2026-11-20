using System.Data;
using System.Text.Json;
using HighConcurrencyCommerce.Application.ProductManagement;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HighConcurrencyCommerce.Infrastructure.ProductManagement;

public sealed class PostgresProductManagementService(StoreDbContext db, IProductMediaStorage mediaStorage, ILogger<PostgresProductManagementService> logger) : IProductManagementService
{
    private static ProductDraftDto ToDraftDto(Product p) =>
        new(
            p.Id,
            p.Title,
            p.Description,
            p.CategoryId,
            p.BrandId,
            p.PublicationStatus,
            p.CreatedAt,
            p.PublishedAt,
            p.StatusChangedAt,
            p.StatusChangedBy,
            p.StatusReason,
            p.ProductSkus
                .OrderBy(s => s.SkuCode)
                .Select(s => new ProductSkuDto(s.Id, s.ProductId, s.SkuCode, s.OptionLabel, s.RegularPrice, s.SkuStatus))
                .ToList(),
            p.ProductImages
                .OrderBy(img => img.DisplayOrder)
                .ThenBy(img => img.CreatedAt)
                .Select(img => new ProductImageItemDto(img.Id, img.ProductId, img.FileKey, img.AltText, img.DisplayOrder, img.ImageStatus))
                .ToList(),
            p.ProductSpecifications.OrderBy(x => x.DisplayOrder).ThenBy(x => x.Id)
                .Select(x => new ProductSpecificationDto(x.Id, x.ProductId, x.SpecKey, x.DisplayName, x.SpecValue, x.DisplayOrder)).ToList());

    public async Task<IReadOnlyList<ProductDraftDto>> GetAllProductsAsync(CancellationToken ct = default)
    {
        var products = await db.Products
            .AsNoTracking()
            .Include(p => p.ProductSkus)
            .Include(p => p.ProductImages)
            .Include(p => p.ProductSpecifications)
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
            .Include(p => p.ProductSpecifications)
            .FirstOrDefaultAsync(p => p.Id == productId, ct);

        if (product is null)
        {
            throw new ProductManagementFailure("NOT_FOUND", "Không tìm thấy sản phẩm.");
        }

        return ToDraftDto(product);
    }

    public async Task<ProductDraftDto> CreateDraftAsync(Guid staffId, CreateProductDraftRequest request, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (request.CategoryId.HasValue)
        {
            var categoryExists = await db.Categories.AnyAsync(c => c.Id == request.CategoryId.Value, ct);
            if (!categoryExists) throw new ProductManagementFailure("NOT_FOUND", "Danh mục không tồn tại.");
        }
        if (request.BrandId.HasValue)
            await LockActiveBrandAsync(request.BrandId.Value, ct);

        var product = new Product
        {
            Id = Guid.NewGuid(),
            CreatedBy = staffId,
            Title = request.Title.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            CategoryId = request.CategoryId,
            BrandId = request.BrandId,
            PublicationStatus = "DRAFT",
            StatusChangedAt = DateTime.UtcNow,
            StatusChangedBy = staffId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Products.Add(product);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

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
        if (request.BrandId.HasValue && request.BrandId != product.BrandId)
            await LockActiveBrandAsync(request.BrandId.Value, ct);

        product.Title = request.Title.Trim();
        product.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        product.CategoryId = request.CategoryId;
        product.BrandId = request.BrandId;
        product.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        await db.Entry(product).Collection(p => p.ProductSkus).LoadAsync(ct);
        await db.Entry(product).Collection(p => p.ProductImages).LoadAsync(ct);
        await db.Entry(product).Collection(p => p.ProductSpecifications).LoadAsync(ct);

        await tx.CommitAsync(ct);
        return ToDraftDto(product);
    }

    private async Task LockActiveBrandAsync(Guid brandId, CancellationToken ct)
    {
        var brand = await db.Brands.FromSqlInterpolated($"SELECT * FROM store.brands WHERE id = {brandId} FOR KEY SHARE")
            .AsNoTracking().SingleOrDefaultAsync(ct);
        if (brand is null || !brand.IsActive)
            throw new ProductManagementFailure("NOT_FOUND", "Thương hiệu không tồn tại hoặc đang ngừng sử dụng.");
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

    public async Task<ProductImageItemDto> UploadImageAsync(Guid staffId, Guid productId, Stream content, string originalFileName, string declaredContentType, long length, string? altText, int displayOrder, CancellationToken ct = default)
    {
        if (altText?.Length > 240 || displayOrder is < 0 or > 10000)
            throw new ProductManagementFailure("VALIDATION_ERROR", "Alt text hoặc thứ tự ảnh không hợp lệ.");
        var stored = await mediaStorage.StoreAsync(productId, content, originalFileName, declaredContentType, length, ct);
        try
        {
            return await AddImageAsync(staffId, productId, new AddProductImageRequest
            {
                FileKey = stored.PublicPath,
                AltText = altText,
                DisplayOrder = displayOrder
            }, ct);
        }
        catch
        {
            await mediaStorage.DeleteAsync(stored.StorageKey, CancellationToken.None);
            throw;
        }
    }

    public async Task<ProductImageItemDto> UpdateImageAsync(Guid staffId, Guid productId, Guid imageId, UpdateProductImageRequest request, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var product = await db.Products.FromSqlInterpolated($"SELECT * FROM store.products WHERE id={productId} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw new ProductManagementFailure("NOT_FOUND", "Không tìm thấy sản phẩm.");
        if (product.PublicationStatus != "DRAFT") throw new ProductManagementFailure("INVALID_STATE", "Chỉ có thể sửa ảnh của sản phẩm DRAFT.");
        var image = await db.ProductImages.SingleOrDefaultAsync(x => x.Id == imageId && x.ProductId == productId, ct)
            ?? throw new ProductManagementFailure("NOT_FOUND", "Không tìm thấy ảnh.");
        image.AltText = string.IsNullOrWhiteSpace(request.AltText) ? null : request.AltText.Trim();
        image.DisplayOrder = request.DisplayOrder;
        image.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(image.Id, image.ProductId, image.FileKey, image.AltText, image.DisplayOrder, image.ImageStatus);
    }

    public async Task<IReadOnlyList<ProductImageItemDto>> ReorderImagesAsync(Guid staffId, Guid productId, ReorderProductImagesRequest request, CancellationToken ct = default)
    {
        if (request.ImageIds.Count != request.ImageIds.Distinct().Count())
            throw new ProductManagementFailure("VALIDATION_ERROR", "Danh sách ảnh không được chứa ID trùng.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var product = await db.Products.FromSqlInterpolated($"SELECT * FROM store.products WHERE id={productId} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw new ProductManagementFailure("NOT_FOUND", "Không tìm thấy sản phẩm.");
        if (product.PublicationStatus != "DRAFT") throw new ProductManagementFailure("INVALID_STATE", "Chỉ có thể sắp xếp ảnh của sản phẩm DRAFT.");
        var images = await db.ProductImages.Where(x => x.ProductId == productId).ToListAsync(ct);
        if (images.Count != request.ImageIds.Count || images.Any(x => !request.ImageIds.Contains(x.Id)))
            throw new ProductManagementFailure("CONFLICT", "Gallery đã thay đổi; vui lòng tải lại trước khi sắp xếp.");
        var order = request.ImageIds.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        var now = DateTime.UtcNow;
        foreach (var image in images) { image.DisplayOrder = order[image.Id]; image.UpdatedAt = now; }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return images.OrderBy(x => x.DisplayOrder).Select(x => new ProductImageItemDto(x.Id, x.ProductId, x.FileKey, x.AltText, x.DisplayOrder, x.ImageStatus)).ToList();
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

        var managedStorageKey = mediaStorage.TryGetStorageKey(image.FileKey, out var key) ? key : null;
        db.ProductImages.Remove(image);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (managedStorageKey is not null)
        {
            try { await mediaStorage.DeleteAsync(managedStorageKey, ct); }
            catch (Exception ex) { logger.LogWarning(ex, "Unable to delete managed product media {StorageKey}; orphan cleanup can remove it later.", managedStorageKey); }
        }
    }

    public async Task<ProductSpecificationDto> UpsertSpecificationAsync(Guid staffId, Guid productId, string key, UpsertProductSpecificationRequest request, string correlationId, CancellationToken ct = default)
    {
        var normalizedKey = key.Trim().ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(normalizedKey, "^[a-z0-9]+(?:_[a-z0-9]+)*$") || normalizedKey.Length > 80)
            throw new ProductManagementFailure("VALIDATION_ERROR", "Khóa thông số phải dùng snake_case và tối đa 80 ký tự.");
        if (string.IsNullOrWhiteSpace(request.DisplayName) || string.IsNullOrWhiteSpace(request.Value))
            throw new ProductManagementFailure("VALIDATION_ERROR", "Tên hiển thị và giá trị thông số không được để trống.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var product = await db.Products.FromSqlInterpolated($"SELECT * FROM store.products WHERE id={productId} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw new ProductManagementFailure("NOT_FOUND", "Không tìm thấy sản phẩm.");
        if (product.PublicationStatus != "DRAFT") throw new ProductManagementFailure("INVALID_STATE", "Chỉ có thể sửa thông số của sản phẩm DRAFT.");
        var now = DateTime.UtcNow;
        var spec = await db.ProductSpecifications.SingleOrDefaultAsync(x => x.ProductId == productId && x.SpecKey == normalizedKey, ct);
        var before = spec is null ? null : JsonSerializer.Serialize(new { spec.SpecKey, spec.DisplayName, spec.SpecValue, spec.DisplayOrder });
        if (spec is null)
        {
            spec = new ProductSpecification { Id = Guid.NewGuid(), ProductId = productId, SpecKey = normalizedKey, CreatedAt = now };
            db.ProductSpecifications.Add(spec);
        }
        spec.DisplayName = request.DisplayName.Trim(); spec.SpecValue = request.Value.Trim(); spec.DisplayOrder = request.DisplayOrder; spec.UpdatedAt = now;
        db.AuditLogs.Add(new AuditLog { Id = Guid.NewGuid(), ActorId = staffId, ActorType = "USER", Action = before is null ? "PRODUCT_SPEC_CREATED" : "PRODUCT_SPEC_UPDATED", EntityType = "Product", EntityId = productId, BeforeData = before, AfterData = JsonSerializer.Serialize(new { spec.SpecKey, spec.DisplayName, spec.SpecValue, spec.DisplayOrder }), CorrelationId = correlationId, OccurredAt = now, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(spec.Id, spec.ProductId, spec.SpecKey, spec.DisplayName, spec.SpecValue, spec.DisplayOrder);
    }

    public async Task DeleteSpecificationAsync(Guid staffId, Guid productId, string key, string correlationId, CancellationToken ct = default)
    {
        var normalizedKey = key.Trim().ToLowerInvariant();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var product = await db.Products.FromSqlInterpolated($"SELECT * FROM store.products WHERE id={productId} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw new ProductManagementFailure("NOT_FOUND", "Không tìm thấy sản phẩm.");
        if (product.PublicationStatus != "DRAFT") throw new ProductManagementFailure("INVALID_STATE", "Chỉ có thể xóa thông số của sản phẩm DRAFT.");
        var spec = await db.ProductSpecifications.SingleOrDefaultAsync(x => x.ProductId == productId && x.SpecKey == normalizedKey, ct)
            ?? throw new ProductManagementFailure("NOT_FOUND", "Không tìm thấy thông số.");
        var now = DateTime.UtcNow;
        db.ProductSpecifications.Remove(spec);
        db.AuditLogs.Add(new AuditLog { Id = Guid.NewGuid(), ActorId = staffId, ActorType = "USER", Action = "PRODUCT_SPEC_DELETED", EntityType = "Product", EntityId = productId, BeforeData = JsonSerializer.Serialize(new { spec.SpecKey, spec.DisplayName, spec.SpecValue, spec.DisplayOrder }), CorrelationId = correlationId, OccurredAt = now, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
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

        var conflictingOffer = await db.FlashSaleOffers
            .Include(o => o.Campaign)
            .Where(o => o.ProductSkuId == skuId
                && (o.Campaign.CampaignStatus == "ACTIVE" || o.Campaign.CampaignStatus == "SCHEDULED")
                && o.SalePrice >= request.Price)
            .FirstOrDefaultAsync(ct);

        if (conflictingOffer is not null)
        {
            throw new ProductManagementFailure("PRICE_CONFLICT_WITH_FLASH_SALE",
                $"Không thể cập nhật giá ({request.Price:N0} đ) thấp hơn hoặc bằng giá ưu đãi Flash Sale ({conflictingOffer.SalePrice:N0} đ) đang hoạt động hoặc đã lên lịch.");
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
        product.StatusChangedAt = now;
        product.StatusChangedBy = adminId;
        product.StatusReason = null;
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

