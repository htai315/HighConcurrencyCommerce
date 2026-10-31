using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HighConcurrencyCommerce.Application.Checkout;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HighConcurrencyCommerce.Infrastructure.Checkout;

public sealed class PostgresCheckoutService(StoreDbContext db, TimeProvider timeProvider) : ICheckoutService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CheckoutExecutionResult> CheckoutAsync(
        Guid customerId,
        string idempotencyKey,
        CreateCheckoutRequest request,
        CancellationToken ct = default)
    {
        ValidatePreflight(request);
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
            "SELECT id AS \"Value\" FROM store.user_accounts WHERE id = {0} AND account_status = 'ACTIVE' FOR UPDATE", customerId)
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
            var result = await CreateOrderAsync(customerId, request, now, ct);
            checkoutRecord.RequestStatus = "SUCCEEDED";
            checkoutRecord.OrderId = result.OrderId;
            checkoutRecord.ResponseSnapshot = JsonSerializer.Serialize(result, JsonOptions);
            checkoutRecord.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new CheckoutExecutionResult(result, false);
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

        var skuIds = request.Lines.Select(x => x.ProductSkuId).OrderBy(x => x).ToArray();
        var lockedSkuIds = await db.Database.SqlQuery<Guid>(
            $"SELECT id AS \"Value\" FROM store.product_skus WHERE id = ANY({skuIds}) ORDER BY id FOR UPDATE")
            .ToListAsync(ct);
        if (lockedSkuIds.Count != skuIds.Length)
            throw new BusinessRejection("SKU_NOT_AVAILABLE", "Một hoặc nhiều SKU không tồn tại hoặc không còn được bán.", 409);

        await db.Database.SqlQuery<Guid>(
            $"SELECT product_sku_id AS \"Value\" FROM store.inventory_balances WHERE product_sku_id = ANY({skuIds}) ORDER BY product_sku_id FOR UPDATE")
            .ToListAsync(ct);

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

                var effectiveActive = offer.Campaign.CampaignStatus is "ACTIVE" or "SCHEDULED"
                    && now >= offer.Campaign.StartsAt && now < offer.Campaign.EndsAt;
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
        var total = itemDtos.Sum(x => x.LineTotal);

        db.SalesOrders.Add(new SalesOrder
        {
            Id = orderId, CustomerId = customerId, ShippingAddressId = address.Id,
            OrderNumber = $"HC-{now:yyyyMMdd}-{orderId.ToString("N")[..12].ToUpperInvariant()}",
            PaymentMethod = request.PaymentMethod, OrderStatus = orderStatus,
            ShippingAddressSnapshot = JsonSerializer.Serialize(addressSnapshot, JsonOptions),
            TotalAmount = total, DeadlineAt = deadline, CreatedAt = now, UpdatedAt = now
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
        return new CheckoutResultDto(orderId, orderNumber, orderStatus, request.PaymentMethod, paymentStatus,
            total, deadline, addressSnapshot, itemDtos);
    }

    private static void ValidatePreflight(CreateCheckoutRequest request)
    {
        if (request.PaymentMethod == "PAYOS")
            throw new CheckoutFailure("PAYOS_DEFERRED", "PayOS đang tạm hoãn cho đến khi hoàn tất kiểm chứng sandbox.", 409);
        if (request.Lines.Select(x => x.ProductSkuId).Distinct().Count() != request.Lines.Count)
            throw new CheckoutFailure("DUPLICATE_SKU", "Mỗi SKU chỉ được xuất hiện một lần trong checkout.");
        if (request.Lines.Any(x => x.ProductSkuId == Guid.Empty) || request.ShippingAddressId == Guid.Empty)
            throw new CheckoutFailure("VALIDATION_ERROR", "SKU và địa chỉ không được để trống.");
        if (request.Lines.Any(x => x.FlashSaleOfferId == Guid.Empty))
            throw new CheckoutFailure("VALIDATION_ERROR", "FlashSaleOfferId không hợp lệ.");
    }

    private static string ComputePayloadHash(CreateCheckoutRequest request)
    {
        var canonical = new
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
