using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HighConcurrencyCommerce.Application.Checkout;
using HighConcurrencyCommerce.Application.Shipping;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using HighConcurrencyCommerce.Infrastructure.Payments;
using Microsoft.Extensions.Options;

namespace HighConcurrencyCommerce.Infrastructure.Checkout;

public sealed class PostgresCheckoutService(
    StoreDbContext db,
    TimeProvider timeProvider,
    IShippingFeeCalculator shippingFeeCalculator,
    IProvinceKeyNormalizer provinceKeyNormalizer,
    IOptions<PayOsOptions> payOsOptions) : ICheckoutService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CheckoutExecutionResult> CheckoutAsync(
        Guid customerId,
        string idempotencyKey,
        CreateCheckoutRequest request,
        CancellationToken ct = default)
    {
        if (request.ShippingMethodId == Guid.Empty || request.ShippingMethodRevision < 1)
            return await ReplayLegacyAsync(customerId, idempotencyKey, request, ct);

        ValidatePreflight(request, payOsOptions.Value.Enabled);
        var payloadHash = ComputePayloadHash(request);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ExecuteAsync(customerId, idempotencyKey, payloadHash, request, ct);
            }
            catch (Exception ex) when (IsRetryable(ex) && attempt < 3)
            {
                db.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(35 * attempt + Random.Shared.Next(10, 45)), ct);
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.QueryCanceled)
            {
                throw new CheckoutFailure("CHECKOUT_IN_PROGRESS", "Một checkout khác của tài khoản đang được xử lý. Vui lòng thử lại sau.", 409);
            }
            catch (Exception ex) when (IsRetryable(ex))
            {
                throw new CheckoutFailure("CONCURRENCY_CONFLICT", "Checkout gặp xung đột đồng thời. Vui lòng thử lại với cùng Idempotency-Key.", 409);
            }
        }
    }

    private async Task<CheckoutExecutionResult> ReplayLegacyAsync(
        Guid customerId,
        string idempotencyKey,
        CreateCheckoutRequest request,
        CancellationToken ct)
    {
        var existing = await db.CheckoutRequests.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CustomerId == customerId && x.IdempotencyKey == idempotencyKey, ct);
        if (existing is null)
            throw new CheckoutFailure("VALIDATION_ERROR",
                "Phương thức, revision và phí giao hàng báo giá là bắt buộc cho checkout mới.");

        var legacyHash = ComputeLegacyPayloadHash(request);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(existing.PayloadHash), Encoding.ASCII.GetBytes(legacyHash)))
            throw new CheckoutFailure("IDEMPOTENCY_KEY_REUSED",
                "Idempotency-Key đã được dùng cho nội dung checkout khác.", 409);

        if (existing.ResponseSnapshot is null)
            throw new CheckoutFailure("CHECKOUT_IN_PROGRESS",
                "Checkout legacy có cùng Idempotency-Key đang được xử lý.", 409);
        if (existing.RequestStatus == "SUCCEEDED")
        {
            var result = JsonSerializer.Deserialize<CheckoutResultDto>(existing.ResponseSnapshot, JsonOptions)
                ?? throw new InvalidOperationException("Legacy checkout success snapshot is invalid.");
            return new CheckoutExecutionResult(result, true);
        }
        if (existing.RequestStatus == "REJECTED")
        {
            var rejection = JsonSerializer.Deserialize<RejectionSnapshot>(existing.ResponseSnapshot, JsonOptions)
                ?? throw new InvalidOperationException("Legacy checkout rejection snapshot is invalid.");
            throw new CheckoutFailure(rejection.Code, rejection.Message, rejection.HttpStatus, true);
        }
        throw new CheckoutFailure("CHECKOUT_IN_PROGRESS",
            "Checkout legacy có cùng Idempotency-Key đang được xử lý.", 409);
    }

    private async Task<CheckoutExecutionResult> ExecuteAsync(
        Guid customerId,
        string idempotencyKey,
        string payloadHash,
        CreateCheckoutRequest request,
        CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'", ct);

        var lockedCustomer = await db.Database.SqlQueryRaw<Guid>(
            "SELECT id AS \"Value\" FROM store.user_accounts WHERE id = {0} AND account_status = 'ACTIVE' FOR NO KEY UPDATE", customerId)
            .FirstOrDefaultAsync(ct);
        if (lockedCustomer == Guid.Empty)
            throw new CheckoutFailure("CUSTOMER_NOT_AVAILABLE", "Tài khoản không thể tạo đơn hàng.", 409);

        var existing = await db.CheckoutRequests
            .FirstOrDefaultAsync(x => x.CustomerId == customerId && x.IdempotencyKey == idempotencyKey, ct);
        if (existing is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(existing.PayloadHash), Encoding.ASCII.GetBytes(payloadHash)))
                throw new CheckoutFailure("IDEMPOTENCY_KEY_REUSED", "Idempotency-Key đã được dùng cho nội dung checkout khác.", 409);

            if (existing.RequestStatus == "SUCCEEDED" && existing.ResponseSnapshot is not null)
            {
                var result = JsonSerializer.Deserialize<CheckoutResultDto>(existing.ResponseSnapshot, JsonOptions)
                    ?? throw new InvalidOperationException("Checkout success snapshot is invalid.");
                await tx.CommitAsync(ct);
                return new CheckoutExecutionResult(result, true);
            }
            if (existing.RequestStatus == "REJECTED" && existing.ResponseSnapshot is not null)
            {
                var rejection = JsonSerializer.Deserialize<RejectionSnapshot>(existing.ResponseSnapshot, JsonOptions)
                    ?? throw new InvalidOperationException("Checkout rejection snapshot is invalid.");
                await tx.CommitAsync(ct);
                throw new CheckoutFailure(rejection.Code, rejection.Message, rejection.HttpStatus, true);
            }
            throw new CheckoutFailure("CHECKOUT_IN_PROGRESS", "Checkout có cùng Idempotency-Key đang được xử lý.", 409);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var checkoutRecord = new CheckoutRequest
        {
            Id = Guid.NewGuid(), CustomerId = customerId, IdempotencyKey = idempotencyKey,
            PayloadHash = payloadHash, RequestStatus = "PROCESSING", CreatedAt = now, UpdatedAt = now
        };
        db.CheckoutRequests.Add(checkoutRecord);
        await db.SaveChangesAsync(ct);
        await tx.CreateSavepointAsync("checkout_work", ct);

        try
        {
            try
            {
                var result = await CreateOrderAsync(customerId, request, now, ct);
                checkoutRecord.RequestStatus = "SUCCEEDED";
                checkoutRecord.OrderId = result.OrderId;
                checkoutRecord.ResponseSnapshot = JsonSerializer.Serialize(result, JsonOptions);
                checkoutRecord.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return new CheckoutExecutionResult(result, false);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } pex
                                              && pex.ConstraintName?.Contains("shipping_address") == true)
            {
                throw new BusinessRejection("ADDRESS_NOT_FOUND", "Không tìm thấy địa chỉ giao hàng thuộc tài khoản.", 404);
            }
        }
        catch (BusinessRejection rejection)
        {
            await tx.RollbackToSavepointAsync("checkout_work", ct);
            db.ChangeTracker.Clear();
            var persisted = await db.CheckoutRequests.SingleAsync(x => x.Id == checkoutRecord.Id, ct);
            persisted.RequestStatus = "REJECTED";
            persisted.ResponseSnapshot = JsonSerializer.Serialize(
                new RejectionSnapshot(rejection.Status, rejection.Code, rejection.Message), JsonOptions);
            persisted.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            throw new CheckoutFailure(rejection.Code, rejection.Message, rejection.Status);
        }
    }

    private async Task<CheckoutResultDto> CreateOrderAsync(
        Guid customerId,
        CreateCheckoutRequest request,
        DateTime now,
        CancellationToken ct)
    {
        if (request.PaymentMethod == "COD")
        {
            var pendingCod = await db.SalesOrders.CountAsync(x => x.CustomerId == customerId
                && x.PaymentMethod == "COD" && x.OrderStatus == "PENDING_COD_CONFIRMATION", ct);
            if (pendingCod >= 2)
                throw new BusinessRejection("COD_PENDING_LIMIT_EXCEEDED", "Tài khoản đã có tối đa hai đơn COD đang chờ xác nhận.", 409);
        }

        var address = await db.CustomerAddresses.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.ShippingAddressId && x.CustomerId == customerId, ct)
            ?? throw new BusinessRejection("ADDRESS_NOT_FOUND", "Không tìm thấy địa chỉ giao hàng thuộc tài khoản.", 404);

        var shippingMethod = await db.ShippingMethods
            .FromSqlInterpolated($"SELECT * FROM store.shipping_methods WHERE id = {request.ShippingMethodId} FOR SHARE")
            .SingleOrDefaultAsync(ct)
            ?? throw new BusinessRejection("SHIPPING_METHOD_UNAVAILABLE", "Phương thức giao hàng không còn khả dụng.", 409);
        if (shippingMethod.MethodStatus != "ACTIVE")
            throw new BusinessRejection("SHIPPING_METHOD_UNAVAILABLE", "Phương thức giao hàng không còn hoạt động.", 409);
        if (shippingMethod.Revision != request.ShippingMethodRevision)
            throw new BusinessRejection("SHIPPING_QUOTE_CHANGED", "Cấu hình giao hàng đã thay đổi. Vui lòng báo giá lại.", 409);
        var destinationProvinceKey = provinceKeyNormalizer.Normalize(address.Province);

        Voucher? lockedVoucher = null;
        if (!string.IsNullOrWhiteSpace(request.VoucherCode))
        {
            var normalizedVoucherCode = request.VoucherCode.Trim().ToUpperInvariant();
            lockedVoucher = await db.Vouchers
                .FromSqlInterpolated($"SELECT * FROM store.vouchers WHERE upper(code) = {normalizedVoucherCode} FOR UPDATE")
                .FirstOrDefaultAsync(ct);
            if (lockedVoucher is null || lockedVoucher.VoucherStatus == "DRAFT")
                throw new BusinessRejection("VOUCHER_NOT_FOUND", "Mã giảm giá không tồn tại.", 404);
        }

        var skuIds = request.Lines.Select(x => x.ProductSkuId).Distinct().OrderBy(x => x).ToArray();
        var productIds = await db.ProductSkus.AsNoTracking()
            .Where(x => skuIds.Contains(x.Id))
            .Select(x => x.ProductId)
            .Distinct()
            .OrderBy(x => x)
            .ToArrayAsync(ct);
        if (await db.ProductSkus.AsNoTracking().CountAsync(x => skuIds.Contains(x.Id), ct) != skuIds.Length)
            throw new BusinessRejection("SKU_NOT_AVAILABLE", "Một hoặc nhiều SKU không tồn tại hoặc không còn được bán.", 409);

        var lockedProductIds = await db.Database.SqlQuery<Guid>(
            $"SELECT id AS \"Value\" FROM store.products WHERE id = ANY({productIds}) ORDER BY id FOR SHARE")
            .ToListAsync(ct);
        if (lockedProductIds.Count != productIds.Length)
            throw new BusinessRejection("PRODUCT_NOT_AVAILABLE", "Một hoặc nhiều sản phẩm không còn được bán.", 409);

        var flashSaleOfferIds = request.Lines
            .Where(x => x.FlashSaleOfferId.HasValue)
            .Select(x => x.FlashSaleOfferId!.Value)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        if (flashSaleOfferIds.Length > 0)
        {
            var offerCampaigns = await db.FlashSaleOffers.AsNoTracking()
                .Where(x => flashSaleOfferIds.Contains(x.Id))
                .Select(x => new { x.Id, x.CampaignId })
                .ToListAsync(ct);
            if (offerCampaigns.Count != flashSaleOfferIds.Length)
                throw new BusinessRejection("FLASH_SALE_NOT_FOUND", "Một hoặc nhiều ưu đãi Flash Sale không tồn tại.", 409);
            var campaignIds = offerCampaigns.Select(x => x.CampaignId).Distinct().OrderBy(x => x).ToArray();
            var lockedCampaignIds = await db.Database.SqlQuery<Guid>(
                $"SELECT id AS \"Value\" FROM store.flash_sale_campaigns WHERE id = ANY({campaignIds}) ORDER BY id FOR UPDATE")
                .ToListAsync(ct);
            if (lockedCampaignIds.Count != campaignIds.Length)
                throw new BusinessRejection("FLASH_SALE_NOT_FOUND", "Chiến dịch Flash Sale không tồn tại.", 409);
            var lockedOfferIds = await db.Database.SqlQuery<Guid>(
                $"SELECT id AS \"Value\" FROM store.flash_sale_offers WHERE id = ANY({flashSaleOfferIds}) ORDER BY id FOR UPDATE")
                .ToListAsync(ct);
            if (lockedOfferIds.Count != flashSaleOfferIds.Length)
                throw new BusinessRejection("FLASH_SALE_NOT_FOUND", "Một hoặc nhiều ưu đãi Flash Sale không tồn tại.", 409);
        }

        var lockedSkuIds = await db.Database.SqlQuery<Guid>(
            $"SELECT id AS \"Value\" FROM store.product_skus WHERE id = ANY({skuIds}) ORDER BY id FOR UPDATE")
            .ToListAsync(ct);
        if (lockedSkuIds.Count != skuIds.Length)
            throw new BusinessRejection("SKU_NOT_AVAILABLE", "Một hoặc nhiều SKU không tồn tại hoặc không còn được bán.", 409);

        await db.Database.SqlQuery<Guid>(
            $"SELECT product_sku_id AS \"Value\" FROM store.inventory_balances WHERE product_sku_id = ANY({skuIds}) ORDER BY product_sku_id FOR UPDATE")
            .ToListAsync(ct);

        // Refresh 'now' after acquiring all locks so Flash Sale and deadlines are evaluated accurately (F05)
        now = timeProvider.GetUtcNow().UtcDateTime;

        var flashSaleOffers = flashSaleOfferIds.Length > 0
            ? await db.FlashSaleOffers
                .Include(x => x.Campaign)
                .Where(x => flashSaleOfferIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, ct)
            : new Dictionary<Guid, FlashSaleOffer>();

        var skus = await db.ProductSkus
            .Include(x => x.Product)
            .Include(x => x.InventoryBalance)
            .Where(x => skuIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);

        foreach (var line in request.Lines)
        {
            var sku = skus[line.ProductSkuId];
            if (sku.SkuStatus != "ACTIVE" || sku.Product.PublicationStatus != "PUBLISHED" || sku.RegularPrice <= 0)
                throw new BusinessRejection("SKU_NOT_AVAILABLE", "Một hoặc nhiều SKU không còn được bán.", 409);
            if (sku.InventoryBalance is null || sku.InventoryBalance.AvailableQty < line.Quantity)
                throw new BusinessRejection("OUT_OF_STOCK", $"Tồn khả dụng không đủ cho SKU {sku.SkuCode}.", 409);

            if (line.FlashSaleOfferId.HasValue)
            {
                var offer = flashSaleOffers[line.FlashSaleOfferId.Value];
                if (offer.ProductSkuId != line.ProductSkuId)
                    throw new BusinessRejection("FLASH_SALE_INVALID_OFFER", "Ưu đãi Flash Sale không khớp với SKU đã chọn.", 409);

                if (offer.SalePrice >= sku.RegularPrice)
                    throw new BusinessRejection("FLASH_SALE_PRICE_INVALID", $"Giá ưu đãi Flash Sale ({offer.SalePrice:N0} đ) phải nhỏ hơn giá niêm yết ({sku.RegularPrice:N0} đ).", 409);

                var lockAcquiredAt = timeProvider.GetUtcNow().UtcDateTime;
                var effectiveActive = offer.Campaign.CampaignStatus is "ACTIVE" or "SCHEDULED"
                    && lockAcquiredAt >= offer.Campaign.StartsAt && lockAcquiredAt < offer.Campaign.EndsAt;
                if (!effectiveActive)
                    throw new BusinessRejection("FLASH_SALE_CAMPAIGN_NOT_ACTIVE", "Chiến dịch Flash Sale chưa mở hoặc đã kết thúc.", 409);

                var availableQuota = offer.QuotaTotal - (offer.QuotaHeld + offer.QuotaSold);
                if (line.Quantity > availableQuota)
                    throw new BusinessRejection("FLASH_SALE_QUOTA_EXCEEDED", $"Số lượng mua vượt quá quota Flash Sale còn lại ({availableQuota}).", 409);

                var customerPurchased = await (
                    from a in db.FlashSaleAllocations
                    join oi in db.SalesOrderItems on a.OrderItemId equals oi.Id
                    join o in db.SalesOrders on oi.OrderId equals o.Id
                    where a.OfferId == offer.Id
                      && o.CustomerId == customerId
                      && (a.AllocationStatus == "HELD" || a.AllocationStatus == "SOLD")
                      && o.OrderStatus != "CANCELLED"
                      && o.OrderStatus != "EXPIRED"
                    select (int?)a.Quantity
                ).SumAsync(ct) ?? 0;

                if (customerPurchased + line.Quantity > offer.PerCustomerLimit)
                {
                    throw new BusinessRejection("FLASH_SALE_CUSTOMER_LIMIT_EXCEEDED",
                        $"Bạn chỉ được mua tối đa {offer.PerCustomerLimit} sản phẩm theo giá Flash Sale (đã mua/đang giữ: {customerPurchased}).", 409);
                }
            }
        }

        var orderId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var deadline = now.Add(request.PaymentMethod == "PAYOS" ? TimeSpan.FromMinutes(15) : TimeSpan.FromHours(24));
        var orderStatus = request.PaymentMethod == "PAYOS" ? "PENDING_PAYMENT" : "PENDING_COD_CONFIRMATION";
        var paymentStatus = request.PaymentMethod == "PAYOS" ? "PENDING" : "COD_PENDING";
        var addressSnapshot = new CheckoutAddressSnapshotDto(
            address.RecipientName, address.Phone, address.AddressLine, address.Ward, address.Province);
        var itemDtos = request.Lines.OrderBy(x => x.ProductSkuId).Select(line =>
        {
            var sku = skus[line.ProductSkuId];
            var unitPrice = line.FlashSaleOfferId.HasValue
                ? flashSaleOffers[line.FlashSaleOfferId.Value].SalePrice
                : sku.RegularPrice;
            return new CheckoutOrderItemDto(sku.Id, sku.Product.Title, sku.SkuCode, sku.OptionLabel,
                line.Quantity, unitPrice, unitPrice * line.Quantity);
        }).ToList();
        var subtotal = itemDtos.Sum(x => x.LineTotal);
        var provinceSurcharge = await db.ShippingMethodProvinceRates.AsNoTracking()
            .Where(x => x.ShippingMethodId == shippingMethod.Id
                        && x.ProvinceKey == destinationProvinceKey && x.IsActive)
            .Select(x => (decimal?)x.SurchargeFee)
            .SingleOrDefaultAsync(ct) ?? 0m;
        var shippingCalculation = shippingFeeCalculator.Calculate(new ShippingFeeCalculationInput(
            subtotal, shippingMethod.BaseFee, provinceSurcharge, shippingMethod.FreeShippingThreshold));
        if (shippingCalculation.ShippingFee != request.QuotedShippingFee)
            throw new BusinessRejection("SHIPPING_QUOTE_CHANGED", "Phí giao hàng đã thay đổi. Vui lòng báo giá lại.", 409);

        decimal voucherDiscount = 0m;
        if (lockedVoucher is not null)
        {
            if (lockedVoucher.VoucherStatus == "PAUSED")
                throw new BusinessRejection("VOUCHER_NOT_ACTIVE", "Mã giảm giá hiện đang tạm dừng áp dụng.", 409);
            if (lockedVoucher.VoucherStatus == "ENDED")
                throw new BusinessRejection("VOUCHER_EXPIRED", "Mã giảm giá đã kết thúc.", 409);
            if (lockedVoucher.VoucherStatus != "ACTIVE")
                throw new BusinessRejection("VOUCHER_NOT_ACTIVE", "Mã giảm giá hiện không khả dụng.", 409);

            if (now < lockedVoucher.StartsAt)
                throw new BusinessRejection("VOUCHER_NOT_STARTED", "Mã giảm giá chưa đến thời gian áp dụng.", 409);
            if (now >= lockedVoucher.EndsAt)
                throw new BusinessRejection("VOUCHER_EXPIRED", "Mã giảm giá đã hết hạn sử dụng.", 409);

            if (lockedVoucher.UsageHeld + lockedVoucher.UsageConsumed >= lockedVoucher.TotalUsageLimit)
                throw new BusinessRejection("VOUCHER_USAGE_LIMIT_REACHED", "Mã giảm giá đã hết lượt sử dụng.", 409);

            var remainingBudget = lockedVoucher.BudgetTotal - (lockedVoucher.BudgetHeld + lockedVoucher.BudgetConsumed);
            if (remainingBudget <= 0)
                throw new BusinessRejection("VOUCHER_BUDGET_EXHAUSTED", "Ngân sách của mã giảm giá đã hết.", 409);

            var customerPurchasedVoucher = await db.VoucherRedemptions
                .CountAsync(r => r.VoucherId == lockedVoucher.Id && r.CustomerId == customerId
                    && (r.RedemptionStatus == "HELD" || r.RedemptionStatus == "CONSUMED"), ct);
            if (customerPurchasedVoucher >= lockedVoucher.PerCustomerLimit)
                throw new BusinessRejection("VOUCHER_CUSTOMER_LIMIT_REACHED",
                    $"Bạn chỉ được sử dụng tối đa {lockedVoucher.PerCustomerLimit} lần cho mã giảm giá này.", 409);

            if (subtotal < lockedVoucher.MinimumOrderAmount)
                throw new BusinessRejection("VOUCHER_MINIMUM_NOT_MET",
                    $"Đơn hàng tối thiểu phải từ {lockedVoucher.MinimumOrderAmount:N0} đ để sử dụng mã này.", 409);

            if (lockedVoucher.DiscountType == "FIXED_AMOUNT")
            {
                voucherDiscount = lockedVoucher.DiscountValue;
            }
            else if (lockedVoucher.DiscountType == "PERCENTAGE")
            {
                var raw = Math.Floor(subtotal * lockedVoucher.DiscountValue / 100m);
                voucherDiscount = Math.Min(raw, lockedVoucher.MaxDiscountAmount ?? raw);
            }
            else
            {
                throw new BusinessRejection("VOUCHER_DISCOUNT_INVALID", "Loại chiết khấu không hợp lệ.", 409);
            }

            if (voucherDiscount <= 0 || voucherDiscount >= subtotal)
                throw new BusinessRejection("VOUCHER_DISCOUNT_INVALID", "Chiết khấu không hợp lệ hoặc làm tổng tiền đơn hàng bằng 0.", 409);

            if (voucherDiscount > remainingBudget)
                throw new BusinessRejection("VOUCHER_BUDGET_EXHAUSTED", "Ngân sách của mã giảm giá không đủ để áp dụng cho đơn hàng.", 409);

            lockedVoucher.UsageHeld += 1;
            lockedVoucher.BudgetHeld += voucherDiscount;
            lockedVoucher.UpdatedAt = now;

            db.VoucherRedemptions.Add(new VoucherRedemption
            {
                Id = Guid.NewGuid(),
                VoucherId = lockedVoucher.Id,
                CustomerId = customerId,
                OrderId = orderId,
                SubtotalSnapshot = subtotal,
                DiscountAmount = voucherDiscount,
                RedemptionStatus = "HELD",
                HeldAt = now,
                ConsumedAt = null,
                ReleasedAt = null,
                ReleaseReason = null,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        var total = subtotal - voucherDiscount + shippingCalculation.ShippingFee;

        db.SalesOrders.Add(new SalesOrder
        {
            Id = orderId, CustomerId = customerId, ShippingAddressId = address.Id,
            OrderNumber = $"HC-{now:yyyyMMdd}-{orderId.ToString("N")[..12].ToUpperInvariant()}",
            PaymentMethod = request.PaymentMethod, OrderStatus = orderStatus,
            ShippingAddressSnapshot = JsonSerializer.Serialize(addressSnapshot, JsonOptions),
            TotalAmount = total, SubtotalAmount = subtotal, VoucherDiscountAmount = voucherDiscount,
            VoucherCodeSnapshot = lockedVoucher?.Code,
            ShippingMethodId = shippingMethod.Id,
            ShippingMethodCodeSnapshot = shippingMethod.Code,
            ShippingMethodNameSnapshot = shippingMethod.Name,
            ShippingFee = shippingCalculation.ShippingFee,
            ShippingEstimatedMinDays = shippingMethod.EstimatedMinDays,
            ShippingEstimatedMaxDays = shippingMethod.EstimatedMaxDays,
            ShippingDestinationProvinceKey = destinationProvinceKey,
            ShippingMethodRevision = shippingMethod.Revision,
            DeadlineAt = deadline, CreatedAt = now, UpdatedAt = now
        });
        db.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(), OrderId = orderId, PaymentMethod = request.PaymentMethod,
            PaymentStatus = paymentStatus, ExpectedAmount = total, CreatedAt = now, UpdatedAt = now
        });
        db.StockReservations.Add(new StockReservation
        {
            Id = reservationId, OrderId = orderId, ReservationStatus = "HELD",
            ReservedAt = now, ExpiresAt = deadline, CreatedAt = now, UpdatedAt = now
        });

        foreach (var line in request.Lines)
        {
            var item = itemDtos.First(x => x.ProductSkuId == line.ProductSkuId);
            var orderItemId = Guid.NewGuid();
            db.SalesOrderItems.Add(new SalesOrderItem
            {
                Id = orderItemId, OrderId = orderId, ProductSkuId = item.ProductSkuId,
                ProductTitleSnapshot = item.ProductTitle, SkuCodeSnapshot = item.SkuCode,
                SkuOptionLabelSnapshot = item.OptionLabel, Quantity = item.Quantity,
                UnitPrice = item.UnitPrice, CreatedAt = now, UpdatedAt = now
            });
            db.StockReservationLines.Add(new StockReservationLine
            {
                Id = Guid.NewGuid(), ReservationId = reservationId, OrderItemId = orderItemId,
                ProductSkuId = item.ProductSkuId, Quantity = item.Quantity, LineStatus = "HELD",
                CreatedAt = now, UpdatedAt = now
            });
            var balance = skus[item.ProductSkuId].InventoryBalance!;
            balance.AvailableQty -= item.Quantity;
            balance.ReservedQty += item.Quantity;
            balance.Revision += 1;
            balance.UpdatedAt = now;
            db.InventoryMovements.Add(new InventoryMovement
            {
                Id = Guid.NewGuid(), ProductSkuId = item.ProductSkuId, MovementType = "RESERVE",
                QtyDelta = item.Quantity, SourceType = "SALES_ORDER", SourceId = orderId,
                ActorId = customerId, Note = "Giữ tồn khi checkout", CreatedAt = now, UpdatedAt = now
            });

            if (line.FlashSaleOfferId.HasValue)
            {
                var offer = flashSaleOffers[line.FlashSaleOfferId.Value];
                offer.QuotaHeld += line.Quantity;
                offer.UpdatedAt = now;
                db.FlashSaleAllocations.Add(new FlashSaleAllocation
                {
                    Id = Guid.NewGuid(),
                    OfferId = offer.Id,
                    OrderItemId = orderItemId,
                    Quantity = line.Quantity,
                    AllocationStatus = "HELD",
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
        }

        var cart = await db.ShoppingCarts.FirstOrDefaultAsync(x => x.CustomerId == customerId && x.CartStatus == "ACTIVE", ct);
        if (cart is not null)
        {
            var checkedOut = await db.CartItems.Where(x => x.CartId == cart.Id && skuIds.Contains(x.ProductSkuId)).ToListAsync(ct);
            db.CartItems.RemoveRange(checkedOut);
            cart.UpdatedAt = now;
        }

        var orderNumber = $"HC-{now:yyyyMMdd}-{orderId.ToString("N")[..12].ToUpperInvariant()}";
        var shippingSnapshot = new CheckoutShippingMethodSnapshotDto(
            shippingMethod.Id, shippingMethod.Code, shippingMethod.Name,
            shippingMethod.EstimatedMinDays, shippingMethod.EstimatedMaxDays,
            shippingMethod.Revision, destinationProvinceKey);
        return new CheckoutResultDto(orderId, orderNumber, orderStatus, request.PaymentMethod, paymentStatus,
            total, deadline, addressSnapshot, itemDtos, subtotal, voucherDiscount, lockedVoucher?.Code,
            shippingCalculation.ShippingFee, shippingSnapshot);
    }

    private static void ValidatePreflight(CreateCheckoutRequest request, bool payOsEnabled)
    {
        if (request.PaymentMethod == "PAYOS" && !payOsEnabled)
            throw new CheckoutFailure("PAYOS_DEFERRED", "PayOS đang tạm hoãn cho đến khi hoàn tất kiểm chứng sandbox.", 409);
        if (request.Lines.Select(x => x.ProductSkuId).Distinct().Count() != request.Lines.Count)
            throw new CheckoutFailure("DUPLICATE_SKU", "Mỗi SKU chỉ được xuất hiện một lần trong checkout.");
        if (request.Lines.Any(x => x.ProductSkuId == Guid.Empty)
            || request.ShippingAddressId == Guid.Empty || request.ShippingMethodId == Guid.Empty)
            throw new CheckoutFailure("VALIDATION_ERROR", "SKU, địa chỉ và phương thức giao hàng không được để trống.");
        if (request.ShippingMethodRevision < 1 || request.QuotedShippingFee < 0
            || request.QuotedShippingFee != decimal.Floor(request.QuotedShippingFee))
            throw new CheckoutFailure("VALIDATION_ERROR", "Revision hoặc phí giao hàng báo giá không hợp lệ.");
        if (request.Lines.Any(x => x.FlashSaleOfferId == Guid.Empty))
            throw new CheckoutFailure("VALIDATION_ERROR", "FlashSaleOfferId không hợp lệ.");
        if (!string.IsNullOrWhiteSpace(request.VoucherCode))
        {
            var trimmed = request.VoucherCode.Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(trimmed, "^[A-Za-z0-9_-]{3,32}$"))
                throw new CheckoutFailure("VALIDATION_ERROR", "Mã voucher không hợp lệ.");
        }
    }

    private static string ComputePayloadHash(CreateCheckoutRequest request)
    {
        var normalizedCode = string.IsNullOrWhiteSpace(request.VoucherCode)
            ? null
            : request.VoucherCode.Trim().ToUpperInvariant();
        var canonicalV3 = new
        {
            version = "checkout-v3",
            voucherCode = normalizedCode,
            shippingAddressId = request.ShippingAddressId.ToString("D").ToLowerInvariant(),
            shippingMethodId = request.ShippingMethodId.ToString("D").ToLowerInvariant(),
            shippingMethodRevision = request.ShippingMethodRevision,
            quotedShippingFee = request.QuotedShippingFee,
            paymentMethod = request.PaymentMethod,
            lines = request.Lines.OrderBy(x => x.ProductSkuId).Select(x => new
            {
                productSkuId = x.ProductSkuId.ToString("D").ToLowerInvariant(),
                quantity = x.Quantity,
                flashSaleOfferId = x.FlashSaleOfferId?.ToString("D").ToLowerInvariant()
            }).ToArray()
        };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonicalV3, JsonOptions)));
    }

    private static string ComputeLegacyPayloadHash(CreateCheckoutRequest request)
    {
        object canonical = string.IsNullOrWhiteSpace(request.VoucherCode)
            ? new
            {
                version = "checkout-v1",
                shippingAddressId = request.ShippingAddressId.ToString("D").ToLowerInvariant(),
                paymentMethod = request.PaymentMethod,
                lines = request.Lines.OrderBy(x => x.ProductSkuId).Select(x => new
                {
                    productSkuId = x.ProductSkuId.ToString("D").ToLowerInvariant(),
                    quantity = x.Quantity,
                    flashSaleOfferId = x.FlashSaleOfferId?.ToString("D").ToLowerInvariant()
                }).ToArray()
            }
            : new
            {
                version = "checkout-v2",
                voucherCode = request.VoucherCode.Trim().ToUpperInvariant(),
                shippingAddressId = request.ShippingAddressId.ToString("D").ToLowerInvariant(),
                paymentMethod = request.PaymentMethod,
                lines = request.Lines.OrderBy(x => x.ProductSkuId).Select(x => new
                {
                    productSkuId = x.ProductSkuId.ToString("D").ToLowerInvariant(),
                    quantity = x.Quantity,
                    flashSaleOfferId = x.FlashSaleOfferId?.ToString("D").ToLowerInvariant()
                }).ToArray()
            };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical, JsonOptions)));
    }

    private static bool IsRetryable(Exception ex) => ex switch
    {
        PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected } => true,
        DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected } } => true,
        _ => false
    };

    private sealed record RejectionSnapshot(int HttpStatus, string Code, string Message);
    private sealed class BusinessRejection(string code, string message, int status) : Exception(message)
    {
        public string Code { get; } = code;
        public int Status { get; } = status;
    }
}
