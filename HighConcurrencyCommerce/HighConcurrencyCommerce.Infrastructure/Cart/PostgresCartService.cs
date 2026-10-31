using System.Data;
using HighConcurrencyCommerce.Application.Cart;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace HighConcurrencyCommerce.Infrastructure.Cart;

public sealed class PostgresCartService(StoreDbContext db) : ICartService
{
    private const int MaxQuantityPerSku = 99;

    public Task<CartDto> GetAsync(Guid customerId, CancellationToken ct = default) => LoadCartAsync(customerId, ct);

    public async Task<CartDto> SetItemAsync(Guid customerId, Guid skuId, SetCartItemRequest request, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await LockCustomerAsync(customerId, ct);

        var skuExists = await db.ProductSkus.AnyAsync(s => s.Id == skuId
            && s.SkuStatus == "ACTIVE" && s.Product.PublicationStatus == "PUBLISHED", ct);
        if (!skuExists)
            throw new CartFailure("SKU_NOT_AVAILABLE", "SKU không tồn tại hoặc không còn được bán.");
        await ValidateOfferAsync(request.FlashSaleOfferId, skuId, ct);

        var cart = await GetOrCreateCartAsync(customerId, ct);
        var item = await db.CartItems.FirstOrDefaultAsync(i => i.CartId == cart.Id && i.ProductSkuId == skuId, ct);
        var now = DateTime.UtcNow;
        if (item is null)
        {
            db.CartItems.Add(new CartItem
            {
                Id = Guid.NewGuid(), CartId = cart.Id, ProductSkuId = skuId,
                FlashSaleOfferId = request.FlashSaleOfferId, Quantity = request.Quantity, CreatedAt = now, UpdatedAt = now
            });
        }
        else
        {
            item.Quantity = request.Quantity;
            item.FlashSaleOfferId = request.FlashSaleOfferId;
            item.UpdatedAt = now;
        }
        cart.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await LoadCartAsync(customerId, ct);
    }

    public async Task<CartDto> RemoveItemAsync(Guid customerId, Guid skuId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await LockCustomerAsync(customerId, ct);
        var cart = await db.ShoppingCarts.FirstOrDefaultAsync(c => c.CustomerId == customerId && c.CartStatus == "ACTIVE", ct);
        if (cart is not null)
        {
            var item = await db.CartItems.FirstOrDefaultAsync(i => i.CartId == cart.Id && i.ProductSkuId == skuId, ct);
            if (item is not null)
            {
                db.CartItems.Remove(item);
                cart.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
        await tx.CommitAsync(ct);
        return await LoadCartAsync(customerId, ct);
    }

    public async Task<MergeCartResultDto> MergeAsync(Guid customerId, MergeCartRequest request, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await LockCustomerAsync(customerId, ct);

        var groups = request.Items.GroupBy(i => i.ProductSkuId).ToList();
        if (groups.Any(g => g.Select(x => x.FlashSaleOfferId).Distinct().Count() > 1))
            throw new CartFailure("FLASH_SALE_OFFER_CONFLICT", "Một SKU không thể được gộp với nhiều ưu đãi Flash Sale khác nhau.");
        var incoming = groups.ToDictionary(g => g.Key, g => new
        {
            Quantity = Math.Min(MaxQuantityPerSku, g.Sum(i => i.Quantity)),
            FlashSaleOfferId = g.Select(x => x.FlashSaleOfferId).Distinct().Single()
        });
        var validSkuIds = await db.ProductSkus
            .Where(s => incoming.Keys.Contains(s.Id) && s.SkuStatus == "ACTIVE" && s.Product.PublicationStatus == "PUBLISHED")
            .Select(s => s.Id).ToListAsync(ct);
        var validSet = validSkuIds.ToHashSet();
        var issues = incoming.Keys.Where(id => !validSet.Contains(id))
            .Select(id => new CartIssueDto(id, "SKU_NOT_AVAILABLE", "SKU không tồn tại hoặc không còn được bán."))
            .ToList();

        if (validSkuIds.Count > 0)
        {
            var cart = await GetOrCreateCartAsync(customerId, ct);
            var existing = await db.CartItems.Where(i => i.CartId == cart.Id && validSkuIds.Contains(i.ProductSkuId))
                .ToDictionaryAsync(i => i.ProductSkuId, ct);
            var now = DateTime.UtcNow;
            foreach (var skuId in validSkuIds)
            {
                var requested = incoming[skuId].Quantity;
                var offerId = incoming[skuId].FlashSaleOfferId;
                await ValidateOfferAsync(offerId, skuId, ct);
                if (existing.TryGetValue(skuId, out var item))
                {
                    var combined = item.Quantity + requested;
                    item.Quantity = Math.Min(MaxQuantityPerSku, combined);
                    item.FlashSaleOfferId = offerId;
                    item.UpdatedAt = now;
                    if (combined > MaxQuantityPerSku)
                        issues.Add(new CartIssueDto(skuId, "QUANTITY_CAPPED", "Tổng số lượng SKU đã được giới hạn ở 99."));
                }
                else
                {
                    db.CartItems.Add(new CartItem
                    {
                        Id = Guid.NewGuid(), CartId = cart.Id, ProductSkuId = skuId,
                        FlashSaleOfferId = offerId, Quantity = requested, CreatedAt = now, UpdatedAt = now
                    });
                }
            }
            cart.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new MergeCartResultDto(await LoadCartAsync(customerId, ct), issues);
    }

    public async Task<CartValidationDto> ValidateAsync(Guid customerId, CancellationToken ct = default)
    {
        var cart = await LoadCartAsync(customerId, ct);
        var issues = new List<CartIssueDto>();
        if (cart.Items.Count == 0)
            issues.Add(new CartIssueDto(null, "EMPTY_CART", "Giỏ hàng đang trống."));
        foreach (var item in cart.Items)
        {
            if (item.AvailabilityCode == "SKU_NOT_AVAILABLE")
                issues.Add(new CartIssueDto(item.ProductSkuId, item.AvailabilityCode, "SKU không còn được bán."));
            else if (item.AvailabilityCode == "INSUFFICIENT_STOCK")
                issues.Add(new CartIssueDto(item.ProductSkuId, item.AvailabilityCode, "Tồn khả dụng hiện không đủ cho số lượng trong giỏ."));
            else if (item.AvailabilityCode == "FLASH_SALE_NOT_AVAILABLE")
                issues.Add(new CartIssueDto(item.ProductSkuId, item.AvailabilityCode, "Ưu đãi Flash Sale không còn hiệu lực hoặc không đủ quota."));
        }
        return new CartValidationDto(issues.Count == 0, cart, issues);
    }

    private async Task LockCustomerAsync(Guid customerId, CancellationToken ct)
    {
        var id = await db.Database.SqlQueryRaw<Guid>(
            "SELECT id AS \"Value\" FROM store.user_accounts WHERE id = {0} FOR UPDATE", customerId)
            .FirstOrDefaultAsync(ct);
        if (id == Guid.Empty) throw new CartFailure("NOT_FOUND", "Không tìm thấy khách hàng.");
    }

    private async Task<ShoppingCart> GetOrCreateCartAsync(Guid customerId, CancellationToken ct)
    {
        var cart = await db.ShoppingCarts.FirstOrDefaultAsync(c => c.CustomerId == customerId, ct);
        if (cart is not null)
        {
            if (cart.CartStatus != "ACTIVE") throw new CartFailure("CART_CLOSED", "Giỏ hàng đã đóng.");
            return cart;
        }
        var now = DateTime.UtcNow;
        cart = new ShoppingCart
        {
            Id = Guid.NewGuid(), CustomerId = customerId, CartStatus = "ACTIVE",
            CreatedAt = now, UpdatedAt = now
        };
        db.ShoppingCarts.Add(cart);
        return cart;
    }

    private async Task<CartDto> LoadCartAsync(Guid customerId, CancellationToken ct)
    {
        var cart = await db.ShoppingCarts.AsNoTracking()
            .Include(c => c.CartItems).ThenInclude(i => i.ProductSku).ThenInclude(s => s.Product).ThenInclude(p => p.ProductImages)
            .Include(c => c.CartItems).ThenInclude(i => i.ProductSku).ThenInclude(s => s.InventoryBalance)
            .Include(c => c.CartItems).ThenInclude(i => i.FlashSaleOffer).ThenInclude(o => o!.Campaign)
            .FirstOrDefaultAsync(c => c.CustomerId == customerId && c.CartStatus == "ACTIVE", ct);
        if (cart is null) return new CartDto(null, [], 0, 0, false, null);

        var items = cart.CartItems.OrderBy(i => i.CreatedAt).ThenBy(i => i.Id).Select(i =>
        {
            var sku = i.ProductSku;
            var purchasable = sku.SkuStatus == "ACTIVE" && sku.Product.PublicationStatus == "PUBLISHED";
            var sufficient = purchasable && sku.InventoryBalance?.AvailableQty >= i.Quantity;
            var now = DateTime.UtcNow;
            var offer = i.FlashSaleOffer;
            var offerValid = offer is null || (offer.ProductSkuId == sku.Id
                && offer.Campaign.CampaignStatus is "ACTIVE" or "SCHEDULED"
                && now >= offer.Campaign.StartsAt && now < offer.Campaign.EndsAt
                && offer.QuotaTotal - offer.QuotaHeld - offer.QuotaSold >= i.Quantity);
            var availability = !purchasable ? "SKU_NOT_AVAILABLE"
                : !sufficient ? "INSUFFICIENT_STOCK"
                : !offerValid ? "FLASH_SALE_NOT_AVAILABLE" : "AVAILABLE";
            var unitPrice = offer is not null && offerValid ? offer.SalePrice : sku.RegularPrice;
            var image = sku.Product.ProductImages.Where(x => x.ImageStatus == "ACTIVE")
                .OrderBy(x => x.DisplayOrder).ThenBy(x => x.CreatedAt).Select(x => x.FileKey).FirstOrDefault();
            return new CartItemDto(sku.Id, sku.ProductId, sku.Product.Title, sku.SkuCode, sku.OptionLabel,
                unitPrice, i.Quantity, unitPrice * i.Quantity, image, availability, i.FlashSaleOfferId);
        }).ToList();
        return new CartDto(cart.Id, items, items.Sum(i => i.Quantity), items.Sum(i => i.LineTotal),
            items.Count > 0 && items.All(i => i.AvailabilityCode == "AVAILABLE"), cart.UpdatedAt);
    }

    private async Task ValidateOfferAsync(Guid? offerId, Guid skuId, CancellationToken ct)
    {
        if (offerId is null) return;
        var now = DateTime.UtcNow;
        var valid = await db.FlashSaleOffers.AsNoTracking().AnyAsync(o => o.Id == offerId
            && o.ProductSkuId == skuId
            && (o.Campaign.CampaignStatus == "ACTIVE" || o.Campaign.CampaignStatus == "SCHEDULED")
            && o.Campaign.StartsAt <= now && o.Campaign.EndsAt > now, ct);
        if (!valid) throw new CartFailure("FLASH_SALE_NOT_AVAILABLE", "Ưu đãi Flash Sale không tồn tại, không khớp SKU hoặc đã hết hiệu lực.");
    }
}
