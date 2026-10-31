using System.Data;
using System.Text.Json;
using HighConcurrencyCommerce.Application.FlashSale;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace HighConcurrencyCommerce.Infrastructure.FlashSale;

public sealed class PostgresFlashSaleService(StoreDbContext db, TimeProvider timeProvider) : IFlashSaleService
{
    private static FlashSaleCampaignDetailDto ToDetailDto(FlashSaleCampaign c) =>
        new(
            c.Id,
            c.Name,
            c.StartsAt,
            c.EndsAt,
            c.CampaignStatus,
            c.CreatedBy,
            c.CreatedAt,
            c.UpdatedAt,
            c.FlashSaleOffers
                .OrderBy(o => o.CreatedAt)
                .Select(o => new FlashSaleOfferDto(
                    o.Id,
                    o.CampaignId,
                    o.ProductSkuId,
                    o.ProductSku.ProductId,
                    o.ProductSku.Product?.Title ?? string.Empty,
                    o.ProductSku.SkuCode,
                    o.ProductSku.OptionLabel,
                    o.ProductSku.RegularPrice,
                    o.SalePrice,
                    o.QuotaTotal,
                    o.QuotaHeld,
                    o.QuotaSold,
                    Math.Max(0, o.QuotaTotal - o.QuotaHeld - o.QuotaSold),
                    o.PerCustomerLimit))
                .ToList());

    private void RecordAudit(
        Guid? actorId,
        string actorType,
        string action,
        string entityType,
        Guid entityId,
        object? before,
        object? after,
        string? correlationId)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = actorId,
            ActorType = actorType,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            BeforeData = before is null ? null : JsonSerializer.Serialize(before),
            AfterData = after is null ? null : JsonSerializer.Serialize(after),
            CorrelationId = correlationId,
            OccurredAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    public async Task<IReadOnlyList<FlashSaleCampaignSummaryDto>> GetAdminCampaignsAsync(CancellationToken ct = default)
    {
        var campaigns = await db.FlashSaleCampaigns
            .AsNoTracking()
            .Include(c => c.FlashSaleOffers)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);

        return campaigns.Select(c => new FlashSaleCampaignSummaryDto(
            c.Id,
            c.Name,
            c.StartsAt,
            c.EndsAt,
            c.CampaignStatus,
            c.CreatedBy,
            c.CreatedAt,
            c.UpdatedAt,
            c.FlashSaleOffers.Count,
            c.FlashSaleOffers.Sum(o => o.QuotaTotal),
            c.FlashSaleOffers.Sum(o => o.QuotaSold)
        )).ToList();
    }

    public async Task<FlashSaleCampaignDetailDto> GetAdminCampaignByIdAsync(Guid id, CancellationToken ct = default)
    {
        var campaign = await db.FlashSaleCampaigns
            .AsNoTracking()
            .Include(c => c.FlashSaleOffers)
                .ThenInclude(o => o.ProductSku)
                    .ThenInclude(s => s.Product)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (campaign is null)
        {
            throw new FlashSaleFailure("NOT_FOUND", "Không tìm thấy chiến dịch Flash Sale.");
        }

        return ToDetailDto(campaign);
    }

    public async Task<FlashSaleCampaignDetailDto> CreateCampaignAsync(
        Guid adminId,
        CreateFlashSaleCampaignRequest request,
        string correlationId,
        CancellationToken ct = default)
    {
        var startsAt = request.StartsAt.ToUniversalTime();
        var endsAt = request.EndsAt.ToUniversalTime();
        var now = timeProvider.GetUtcNow().UtcDateTime;

        if (endsAt <= startsAt)
        {
            throw new FlashSaleFailure("VALIDATION_ERROR", "Thời gian kết thúc phải sau thời gian bắt đầu.");
        }

        if (endsAt <= now)
        {
            throw new FlashSaleFailure("VALIDATION_ERROR", "Thời gian kết thúc phải ở tương lai.");
        }

        var campaign = new FlashSaleCampaign
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            StartsAt = startsAt,
            EndsAt = endsAt,
            CampaignStatus = "DRAFT",
            CreatedBy = adminId,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.FlashSaleCampaigns.Add(campaign);
        RecordAudit(adminId, "USER", "FLASH_SALE_CAMPAIGN_CREATED", "FlashSaleCampaign", campaign.Id, null, new
        {
            name = campaign.Name,
            startsAt = campaign.StartsAt,
            endsAt = campaign.EndsAt,
            status = campaign.CampaignStatus
        }, correlationId);

        await db.SaveChangesAsync(ct);

        return new FlashSaleCampaignDetailDto(
            campaign.Id,
            campaign.Name,
            campaign.StartsAt,
            campaign.EndsAt,
            campaign.CampaignStatus,
            campaign.CreatedBy,
            campaign.CreatedAt,
            campaign.UpdatedAt,
            []);
    }

    public async Task<FlashSaleCampaignDetailDto> UpdateCampaignAsync(
        Guid adminId,
        Guid id,
        UpdateFlashSaleCampaignRequest request,
        string correlationId,
        CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var campaign = await db.FlashSaleCampaigns
            .FromSqlInterpolated($"SELECT * FROM store.flash_sale_campaigns WHERE id = {id} FOR UPDATE")
            .FirstOrDefaultAsync(ct);

        if (campaign is null)
        {
            throw new FlashSaleFailure("NOT_FOUND", "Không tìm thấy chiến dịch Flash Sale.");
        }

        if (campaign.CampaignStatus != "DRAFT")
        {
            throw new FlashSaleFailure("INVALID_STATE_TRANSITION", "Chỉ có thể chỉnh sửa thông tin chiến dịch ở trạng thái DRAFT.");
        }

        var startsAt = request.StartsAt.ToUniversalTime();
        var endsAt = request.EndsAt.ToUniversalTime();
        var now = timeProvider.GetUtcNow().UtcDateTime;

        if (endsAt <= startsAt)
        {
            throw new FlashSaleFailure("VALIDATION_ERROR", "Thời gian kết thúc phải sau thời gian bắt đầu.");
        }

        if (endsAt <= now)
        {
            throw new FlashSaleFailure("VALIDATION_ERROR", "Thời gian kết thúc phải ở tương lai.");
        }

        var beforeData = new
        {
            name = campaign.Name,
            startsAt = campaign.StartsAt,
            endsAt = campaign.EndsAt
        };

        campaign.Name = request.Name.Trim();
        campaign.StartsAt = startsAt;
        campaign.EndsAt = endsAt;
        campaign.UpdatedAt = now;

        var afterData = new
        {
            name = campaign.Name,
            startsAt = campaign.StartsAt,
            endsAt = campaign.EndsAt
        };

        RecordAudit(adminId, "USER", "FLASH_SALE_CAMPAIGN_UPDATED", "FlashSaleCampaign", campaign.Id, beforeData, afterData, correlationId);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return await GetAdminCampaignByIdAsync(id, ct);
    }

    public async Task<FlashSaleOfferDto> AddOfferAsync(
        Guid adminId,
        Guid campaignId,
        CreateFlashSaleOfferRequest request,
        string correlationId,
        CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var campaign = await db.FlashSaleCampaigns
            .FromSqlInterpolated($"SELECT * FROM store.flash_sale_campaigns WHERE id = {campaignId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);

        if (campaign is null)
        {
            throw new FlashSaleFailure("NOT_FOUND", "Không tìm thấy chiến dịch Flash Sale.");
        }

        if (campaign.CampaignStatus != "DRAFT")
        {
            throw new FlashSaleFailure("INVALID_STATE_TRANSITION", "Chỉ có thể thêm sản phẩm vào chiến dịch ở trạng thái DRAFT.");
        }

        var sku = await db.ProductSkus
            .Include(s => s.Product)
            .FirstOrDefaultAsync(s => s.Id == request.ProductSkuId, ct);

        if (sku is null)
        {
            throw new FlashSaleFailure("NOT_FOUND", "Không tìm thấy biến thể SKU.");
        }

        if (sku.SkuStatus != "ACTIVE")
        {
            throw new FlashSaleFailure("VALIDATION_ERROR", "Chỉ có thể đưa SKU ở trạng thái ACTIVE vào Flash Sale.");
        }

        if (sku.Product.PublicationStatus != "PUBLISHED")
        {
            throw new FlashSaleFailure("VALIDATION_ERROR", "Sản phẩm chứa SKU này chưa được Publish.");
        }

        var offerExists = await db.FlashSaleOffers
            .AnyAsync(o => o.CampaignId == campaignId && o.ProductSkuId == request.ProductSkuId, ct);

        if (offerExists)
        {
            throw new FlashSaleFailure("CONFLICT", "Biến thể SKU này đã tồn tại trong chiến dịch.");
        }

        if (request.SalePrice <= 0)
        {
            throw new FlashSaleFailure("VALIDATION_ERROR", "Giá Flash Sale phải lớn hơn 0.");
        }

        if (request.SalePrice >= sku.RegularPrice)
        {
            throw new FlashSaleFailure("VALIDATION_ERROR", $"Giá Flash Sale ({request.SalePrice:N0} đ) phải nhỏ hơn giá gốc ({sku.RegularPrice:N0} đ).");
        }

        if (request.QuotaTotal <= 0)
        {
            throw new FlashSaleFailure("VALIDATION_ERROR", "Số lượng quota phải lớn hơn 0.");
        }

        var balance = await db.InventoryBalances
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.ProductSkuId == request.ProductSkuId, ct);

        var availableQty = balance?.AvailableQty ?? 0;
        if (request.QuotaTotal > availableQty)
        {
            throw new FlashSaleFailure("INSUFFICIENT_STOCK", $"Số lượng quota ({request.QuotaTotal}) vượt quá tồn kho khả dụng ({availableQty}).");
        }

        if (request.PerCustomerLimit <= 0)
        {
            throw new FlashSaleFailure("VALIDATION_ERROR", "Giới hạn mỗi khách hàng phải từ 1 trở lên.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var offer = new FlashSaleOffer
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            ProductSkuId = request.ProductSkuId,
            SalePrice = request.SalePrice,
            QuotaTotal = request.QuotaTotal,
            QuotaHeld = 0,
            QuotaSold = 0,
            PerCustomerLimit = request.PerCustomerLimit,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.FlashSaleOffers.Add(offer);
        campaign.UpdatedAt = now;

        RecordAudit(adminId, "USER", "FLASH_SALE_OFFER_ADDED", "FlashSaleOffer", offer.Id, null, new
        {
            campaignId,
            productSkuId = offer.ProductSkuId,
            salePrice = offer.SalePrice,
            quotaTotal = offer.QuotaTotal,
            perCustomerLimit = offer.PerCustomerLimit
        }, correlationId);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new FlashSaleOfferDto(
            offer.Id,
            offer.CampaignId,
            offer.ProductSkuId,
            sku.ProductId,
            sku.Product.Title,
            sku.SkuCode,
            sku.OptionLabel,
            sku.RegularPrice,
            offer.SalePrice,
            offer.QuotaTotal,
            offer.QuotaHeld,
            offer.QuotaSold,
            offer.QuotaTotal,
            offer.PerCustomerLimit);
    }

    public async Task DeleteOfferAsync(
        Guid adminId,
        Guid campaignId,
        Guid offerId,
        string correlationId,
        CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var campaign = await db.FlashSaleCampaigns
            .FromSqlInterpolated($"SELECT * FROM store.flash_sale_campaigns WHERE id = {campaignId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);

        if (campaign is null)
        {
            throw new FlashSaleFailure("NOT_FOUND", "Không tìm thấy chiến dịch Flash Sale.");
        }

        if (campaign.CampaignStatus != "DRAFT")
        {
            throw new FlashSaleFailure("INVALID_STATE_TRANSITION", "Chỉ có thể xóa sản phẩm khỏi chiến dịch ở trạng thái DRAFT.");
        }

        var offer = await db.FlashSaleOffers
            .FirstOrDefaultAsync(o => o.Id == offerId && o.CampaignId == campaignId, ct);

        if (offer is null)
        {
            throw new FlashSaleFailure("NOT_FOUND", "Không tìm thấy ưu đãi Flash Sale.");
        }

        db.FlashSaleOffers.Remove(offer);
        campaign.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;

        RecordAudit(adminId, "USER", "FLASH_SALE_OFFER_REMOVED", "FlashSaleOffer", offer.Id, new
        {
            campaignId,
            productSkuId = offer.ProductSkuId,
            salePrice = offer.SalePrice,
            quotaTotal = offer.QuotaTotal
        }, null, correlationId);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<FlashSaleCampaignDetailDto> PublishCampaignAsync(
        Guid adminId,
        Guid campaignId,
        string correlationId,
        CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var campaign = await db.FlashSaleCampaigns
            .FromSqlInterpolated($"SELECT * FROM store.flash_sale_campaigns WHERE id = {campaignId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);

        if (campaign is null)
        {
            throw new FlashSaleFailure("NOT_FOUND", "Không tìm thấy chiến dịch Flash Sale.");
        }

        if (campaign.CampaignStatus != "DRAFT")
        {
            throw new FlashSaleFailure("INVALID_STATE_TRANSITION", $"Chiến dịch đang ở trạng thái {campaign.CampaignStatus}, không thể công bố.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (now >= campaign.EndsAt)
        {
            throw new FlashSaleFailure("CANNOT_PUBLISH", "Chiến dịch đã quá thời gian kết thúc.");
        }

        await db.Entry(campaign)
            .Collection(c => c.FlashSaleOffers)
            .Query()
            .Include(o => o.ProductSku)
                .ThenInclude(s => s.Product)
            .LoadAsync(ct);

        if (campaign.FlashSaleOffers.Count == 0)
        {
            throw new FlashSaleFailure("CANNOT_PUBLISH", "Chiến dịch phải có ít nhất 1 sản phẩm khuyến mãi trước khi công bố.");
        }

        foreach (var offer in campaign.FlashSaleOffers)
        {
            var sku = offer.ProductSku;
            if (sku.SkuStatus != "ACTIVE" || sku.Product.PublicationStatus != "PUBLISHED")
            {
                throw new FlashSaleFailure("CANNOT_PUBLISH", $"Sản phẩm {sku.SkuCode} không ở trạng thái mở bán hợp lệ.");
            }

            if (offer.SalePrice >= sku.RegularPrice)
            {
                throw new FlashSaleFailure("CANNOT_PUBLISH", $"Giá sale của SKU {sku.SkuCode} ({offer.SalePrice:N0} đ) phải nhỏ hơn giá gốc ({sku.RegularPrice:N0} đ).");
            }

            var balance = await db.InventoryBalances
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.ProductSkuId == offer.ProductSkuId, ct);

            var availableQty = balance?.AvailableQty ?? 0;
            if (offer.QuotaTotal > availableQty)
            {
                throw new FlashSaleFailure("CANNOT_PUBLISH", $"Quota của SKU {sku.SkuCode} ({offer.QuotaTotal}) vượt quá tồn kho khả dụng ({availableQty}).");
            }
        }

        var newStatus = now < campaign.StartsAt ? "SCHEDULED" : "ACTIVE";
        var beforeStatus = campaign.CampaignStatus;

        campaign.CampaignStatus = newStatus;
        campaign.UpdatedAt = now;

        RecordAudit(adminId, "USER", "FLASH_SALE_CAMPAIGN_PUBLISHED", "FlashSaleCampaign", campaign.Id, new
        {
            status = beforeStatus
        }, new
        {
            status = newStatus,
            offersCount = campaign.FlashSaleOffers.Count
        }, correlationId);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return ToDetailDto(campaign);
    }

    public async Task<FlashSaleCampaignDetailDto> CancelCampaignAsync(
        Guid adminId,
        Guid campaignId,
        CancelFlashSaleCampaignRequest? request,
        string correlationId,
        CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var campaign = await db.FlashSaleCampaigns
            .FromSqlInterpolated($"SELECT * FROM store.flash_sale_campaigns WHERE id = {campaignId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);

        if (campaign is null)
        {
            throw new FlashSaleFailure("NOT_FOUND", "Không tìm thấy chiến dịch Flash Sale.");
        }

        if (campaign.CampaignStatus == "CANCELLED")
        {
            throw new FlashSaleFailure("INVALID_STATE_TRANSITION", "Chiến dịch đã bị hủy trước đó.");
        }

        if (campaign.CampaignStatus == "ENDED")
        {
            throw new FlashSaleFailure("INVALID_STATE_TRANSITION", "Chiến dịch đã kết thúc, không thể hủy.");
        }

        var oldStatus = campaign.CampaignStatus;
        var now = timeProvider.GetUtcNow().UtcDateTime;

        campaign.CampaignStatus = "CANCELLED";
        campaign.UpdatedAt = now;

        RecordAudit(adminId, "USER", "FLASH_SALE_CAMPAIGN_CANCELLED", "FlashSaleCampaign", campaign.Id, new
        {
            status = oldStatus
        }, new
        {
            status = "CANCELLED",
            reason = request?.Reason
        }, correlationId);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return await GetAdminCampaignByIdAsync(campaignId, ct);
    }

    public async Task<FlashSalePerformanceDto> GetCampaignPerformanceAsync(Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await db.FlashSaleCampaigns
            .AsNoTracking()
            .Include(c => c.FlashSaleOffers)
                .ThenInclude(o => o.ProductSku)
                    .ThenInclude(s => s.Product)
            .FirstOrDefaultAsync(c => c.Id == campaignId, ct);

        if (campaign is null)
        {
            throw new FlashSaleFailure("NOT_FOUND", "Không tìm thấy chiến dịch Flash Sale.");
        }

        var offersPerf = campaign.FlashSaleOffers.Select(o =>
        {
            var soldRevenue = o.QuotaSold * o.SalePrice;
            var remainingQuota = Math.Max(0, o.QuotaTotal - o.QuotaHeld - o.QuotaSold);
            return new FlashSaleOfferPerformanceDto(
                o.Id,
                o.ProductSku.SkuCode,
                o.ProductSku.Product?.Title ?? string.Empty,
                o.SalePrice,
                o.QuotaTotal,
                o.QuotaHeld,
                o.QuotaSold,
                remainingQuota,
                soldRevenue);
        }).ToList();

        var totalQuota = offersPerf.Sum(o => o.QuotaTotal);
        var totalHeld = offersPerf.Sum(o => o.QuotaHeld);
        var totalSold = offersPerf.Sum(o => o.QuotaSold);
        var remaining = offersPerf.Sum(o => o.RemainingQuota);
        var estimatedRevenue = offersPerf.Sum(o => o.SoldRevenue);

        return new FlashSalePerformanceDto(
            campaign.Id,
            campaign.Name,
            campaign.CampaignStatus,
            campaign.StartsAt,
            campaign.EndsAt,
            totalQuota,
            totalHeld,
            totalSold,
            remaining,
            estimatedRevenue,
            offersPerf);
    }

    public async Task<IReadOnlyList<PublicFlashSaleCampaignDto>> GetPublicActiveCampaignsAsync(CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var campaigns = await db.FlashSaleCampaigns
            .AsNoTracking()
            .Where(c => (c.CampaignStatus == "ACTIVE" || c.CampaignStatus == "SCHEDULED") && c.EndsAt > now)
            .Include(c => c.FlashSaleOffers)
                .ThenInclude(o => o.ProductSku)
                    .ThenInclude(s => s.Product)
            .OrderBy(c => c.StartsAt)
            .ToListAsync(ct);

        var productIds = campaigns
            .SelectMany(c => c.FlashSaleOffers)
            .Select(o => o.ProductSku.ProductId)
            .Distinct()
            .ToList();

        var images = await db.ProductImages
            .AsNoTracking()
            .Where(img => productIds.Contains(img.ProductId))
            .OrderBy(img => img.DisplayOrder)
            .ThenBy(img => img.CreatedAt)
            .ToListAsync(ct);

        var primaryImageMap = images
            .GroupBy(img => img.ProductId)
            .ToDictionary(g => g.Key, g => g.FirstOrDefault()?.FileKey);

        return campaigns.Select(c =>
        {
            var effectiveStatus = c.CampaignStatus;
            if (effectiveStatus == "SCHEDULED" && now >= c.StartsAt && now < c.EndsAt)
            {
                effectiveStatus = "ACTIVE";
            }

            var offers = c.FlashSaleOffers
                .Where(o => o.ProductSku.SkuStatus == "ACTIVE" && o.ProductSku.Product.PublicationStatus == "PUBLISHED")
                .OrderBy(o => o.CreatedAt)
                .Select(o =>
                {
                    var regular = o.ProductSku.RegularPrice;
                    var discount = regular > 0
                        ? (int)Math.Round((regular - o.SalePrice) / regular * 100m)
                        : 0;
                    var rem = Math.Max(0, o.QuotaTotal - o.QuotaHeld - o.QuotaSold);
                    var img = primaryImageMap.GetValueOrDefault(o.ProductSku.ProductId);

                    return new PublicFlashSaleOfferDto(
                        o.Id,
                        o.ProductSkuId,
                        o.ProductSku.ProductId,
                        o.ProductSku.Product.Title,
                        o.ProductSku.SkuCode,
                        o.ProductSku.OptionLabel,
                        regular,
                        o.SalePrice,
                        discount,
                        o.QuotaTotal,
                        rem,
                        o.PerCustomerLimit,
                        img);
                }).ToList();

            return new PublicFlashSaleCampaignDto(
                c.Id,
                c.Name,
                c.StartsAt,
                c.EndsAt,
                effectiveStatus,
                offers);
        }).ToList();
    }

    public async Task<PublicFlashSaleCampaignDto?> GetPublicCampaignByIdAsync(Guid id, CancellationToken ct = default)
    {
        var campaigns = await GetPublicActiveCampaignsAsync(ct);
        return campaigns.FirstOrDefault(c => c.Id == id);
    }

    public async Task<int> SynchronizeLifecycleAsync(int batchSize = 100, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var ids = await db.FlashSaleCampaigns.AsNoTracking()
            .Where(c => (c.CampaignStatus == "SCHEDULED" && c.StartsAt <= now)
                || (c.CampaignStatus == "ACTIVE" && c.EndsAt <= now))
            .OrderBy(c => c.StartsAt).ThenBy(c => c.Id)
            .Select(c => c.Id).Take(Math.Clamp(batchSize, 1, 500)).ToListAsync(ct);
        var changed = 0;
        foreach (var id in ids)
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var campaign = await db.FlashSaleCampaigns
                .FromSqlInterpolated($"SELECT * FROM store.flash_sale_campaigns WHERE id = {id} FOR UPDATE")
                .SingleOrDefaultAsync(ct);
            if (campaign is null) { await tx.CommitAsync(ct); continue; }
            var next = EffectiveStoredStatus(campaign, now);
            if (next == campaign.CampaignStatus) { await tx.CommitAsync(ct); continue; }
            var previous = campaign.CampaignStatus;
            campaign.CampaignStatus = next;
            campaign.UpdatedAt = now;
            RecordAudit(null, "WORKER", $"FLASH_SALE_CAMPAIGN_{next}", "FlashSaleCampaign", campaign.Id,
                new { status = previous }, new { status = next }, $"flash-sale-lifecycle:{campaign.Id:N}:{next}");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            changed++;
        }
        return changed;
    }

    private static string EffectiveStoredStatus(FlashSaleCampaign campaign, DateTime now)
    {
        if (campaign.CampaignStatus == "SCHEDULED" && now >= campaign.EndsAt) return "ENDED";
        if (campaign.CampaignStatus == "SCHEDULED" && now >= campaign.StartsAt) return "ACTIVE";
        if (campaign.CampaignStatus == "ACTIVE" && now >= campaign.EndsAt) return "ENDED";
        return campaign.CampaignStatus;
    }
}

