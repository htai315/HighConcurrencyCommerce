using HighConcurrencyCommerce.Application.Shipping;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace HighConcurrencyCommerce.Infrastructure.Shipping;

public sealed class PostgresShippingQuoteService(
    StoreDbContext db,
    IShippingFeeCalculator calculator,
    IProvinceKeyNormalizer provinceKeyNormalizer,
    TimeProvider timeProvider) : IShippingQuoteService
{
    public async Task<ShippingQuoteResultDto> QuoteAsync(
        Guid customerId,
        ShippingQuoteRequest request,
        CancellationToken ct = default)
    {
        Validate(request);

        var address = await db.CustomerAddresses.AsNoTracking()
            .Where(x => x.Id == request.ShippingAddressId && x.CustomerId == customerId)
            .Select(x => new { x.Id, x.Province })
            .SingleOrDefaultAsync(ct)
            ?? throw new ShippingFailure("ADDRESS_NOT_FOUND", "Không tìm thấy địa chỉ giao hàng thuộc tài khoản.", 404);

        var provinceKey = provinceKeyNormalizer.Normalize(address.Province);
        var skuIds = request.Lines.Select(x => x.ProductSkuId).OrderBy(x => x).ToArray();
        var skus = await db.ProductSkus.AsNoTracking()
            .Include(x => x.Product)
            .Include(x => x.InventoryBalance)
            .Where(x => skuIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);

        if (skus.Count != skuIds.Length)
            throw new ShippingFailure("SKU_NOT_AVAILABLE", "Một hoặc nhiều SKU không tồn tại hoặc không còn được bán.", 409);

        var offerIds = request.Lines
            .Where(x => x.FlashSaleOfferId.HasValue)
            .Select(x => x.FlashSaleOfferId!.Value)
            .Distinct()
            .ToArray();
        var offers = offerIds.Length == 0
            ? new Dictionary<Guid, FlashSaleOffer>()
            : await db.FlashSaleOffers.AsNoTracking()
                .Include(x => x.Campaign)
                .Where(x => offerIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, ct);

        if (offers.Count != offerIds.Length)
            throw new ShippingFailure("FLASH_SALE_NOT_FOUND", "Một hoặc nhiều ưu đãi Flash Sale không tồn tại.", 409);

        var customerOfferAllocations = offerIds.Length == 0
            ? new Dictionary<Guid, int>()
            : await (
                from allocation in db.FlashSaleAllocations.AsNoTracking()
                join orderItem in db.SalesOrderItems.AsNoTracking() on allocation.OrderItemId equals orderItem.Id
                join order in db.SalesOrders.AsNoTracking() on orderItem.OrderId equals order.Id
                where offerIds.Contains(allocation.OfferId)
                      && order.CustomerId == customerId
                      && (allocation.AllocationStatus == "HELD" || allocation.AllocationStatus == "SOLD")
                      && order.OrderStatus != "CANCELLED"
                      && order.OrderStatus != "EXPIRED"
                group allocation by allocation.OfferId into allocations
                select new { OfferId = allocations.Key, Quantity = allocations.Sum(x => x.Quantity) }
            ).ToDictionaryAsync(x => x.OfferId, x => x.Quantity, ct);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        decimal merchandiseSubtotal = 0m;
        foreach (var line in request.Lines)
        {
            var sku = skus[line.ProductSkuId];
            if (sku.SkuStatus != "ACTIVE" || sku.Product.PublicationStatus != "PUBLISHED" || sku.RegularPrice <= 0)
                throw new ShippingFailure("SKU_NOT_AVAILABLE", "Một hoặc nhiều SKU không còn được bán.", 409);
            if (sku.InventoryBalance is null || sku.InventoryBalance.AvailableQty < line.Quantity)
                throw new ShippingFailure("OUT_OF_STOCK", $"Tồn khả dụng không đủ cho SKU {sku.SkuCode}.", 409);

            var unitPrice = sku.RegularPrice;
            if (line.FlashSaleOfferId is { } offerId)
            {
                var offer = offers[offerId];
                if (offer.ProductSkuId != sku.Id)
                    throw new ShippingFailure("FLASH_SALE_INVALID_OFFER", "Ưu đãi Flash Sale không khớp với SKU đã chọn.", 409);
                if (offer.SalePrice <= 0 || offer.SalePrice >= sku.RegularPrice)
                    throw new ShippingFailure("FLASH_SALE_PRICE_INVALID", "Giá ưu đãi Flash Sale không còn hợp lệ.", 409);

                var isActive = offer.Campaign.CampaignStatus is "ACTIVE" or "SCHEDULED"
                    && now >= offer.Campaign.StartsAt && now < offer.Campaign.EndsAt;
                if (!isActive)
                    throw new ShippingFailure("FLASH_SALE_CAMPAIGN_NOT_ACTIVE", "Chiến dịch Flash Sale chưa mở hoặc đã kết thúc.", 409);
                if (line.Quantity > offer.QuotaTotal - offer.QuotaHeld - offer.QuotaSold)
                    throw new ShippingFailure("FLASH_SALE_QUOTA_EXCEEDED", "Số lượng mua vượt quá quota Flash Sale còn lại.", 409);

                var alreadyAllocated = customerOfferAllocations.GetValueOrDefault(offer.Id);
                if (alreadyAllocated + line.Quantity > offer.PerCustomerLimit)
                    throw new ShippingFailure("FLASH_SALE_CUSTOMER_LIMIT_EXCEEDED",
                        $"Bạn chỉ được mua tối đa {offer.PerCustomerLimit} sản phẩm theo giá Flash Sale.", 409);

                unitPrice = offer.SalePrice;
            }

            merchandiseSubtotal = checked(merchandiseSubtotal + unitPrice * line.Quantity);
        }

        var methods = await db.ShippingMethods.AsNoTracking()
            .Where(x => x.MethodStatus == "ACTIVE")
            .Include(x => x.ProvinceRates.Where(rate => rate.IsActive && rate.ProvinceKey == provinceKey))
            .ToListAsync(ct);
        if (methods.Count == 0)
            throw new ShippingFailure("SHIPPING_METHOD_UNAVAILABLE",
                "Hiện chưa có phương thức giao hàng khả dụng cho đơn hàng mới.", 409);

        var quotes = methods.Select(method =>
        {
            var surcharge = method.ProvinceRates.SingleOrDefault()?.SurchargeFee ?? 0m;
            var fee = calculator.Calculate(new ShippingFeeCalculationInput(
                merchandiseSubtotal, method.BaseFee, surcharge, method.FreeShippingThreshold));
            return new
            {
                method.DisplayOrder,
                Quote = new ShippingQuoteDto(
                    method.Id, method.Code, method.Name, method.Description,
                    fee.ShippingFee, method.BaseFee, surcharge, fee.FreeShippingApplied,
                    method.FreeShippingThreshold, method.EstimatedMinDays, method.EstimatedMaxDays,
                    method.Revision, method.IsDefault)
            };
        })
        .OrderByDescending(x => x.Quote.IsDefault)
        .ThenBy(x => x.DisplayOrder)
        .ThenBy(x => x.Quote.ShippingFee)
        .ThenBy(x => x.Quote.ShippingMethodId)
        .Select(x => x.Quote)
        .ToList();

        return new ShippingQuoteResultDto(address.Id, provinceKey, merchandiseSubtotal, quotes);
    }

    private static void Validate(ShippingQuoteRequest request)
    {
        if (request.ShippingAddressId == Guid.Empty || request.Lines is null || request.Lines.Count is < 1 or > 50)
            throw new ShippingFailure("VALIDATION_ERROR", "Địa chỉ và danh sách sản phẩm không hợp lệ.");
        if (request.Lines.Any(x => x.ProductSkuId == Guid.Empty || x.Quantity is < 1 or > 99
                                   || x.FlashSaleOfferId == Guid.Empty))
            throw new ShippingFailure("VALIDATION_ERROR", "SKU, số lượng hoặc ưu đãi Flash Sale không hợp lệ.");
        if (request.Lines.Select(x => x.ProductSkuId).Distinct().Count() != request.Lines.Count)
            throw new ShippingFailure("DUPLICATE_SKU", "Mỗi SKU chỉ được xuất hiện một lần trong yêu cầu báo giá.");
    }
}
