using System.Text.RegularExpressions;
using HighConcurrencyCommerce.Application.Cart;
using HighConcurrencyCommerce.Application.Vouchers;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HighConcurrencyCommerce.Infrastructure.Vouchers;

public sealed partial class PostgresVoucherCustomerService(
    StoreDbContext db,
    ICartService cartService,
    TimeProvider timeProvider) : IVoucherCustomerService
{
    [GeneratedRegex("^[A-Z0-9_-]{3,32}$")]
    private static partial Regex CodeRegex();

    public async Task<VoucherPreviewDto> PreviewVoucherAsync(
        Guid customerId,
        PreviewVoucherRequest request,
        CancellationToken ct = default)
    {
        var raw = request.Code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            throw new VoucherFailure("VALIDATION_ERROR", "Mã voucher không được để trống.", 400);

        var cart = await cartService.GetAsync(customerId, ct);
        if (cart.Items.Count == 0)
            throw new VoucherFailure("VALIDATION_ERROR", "Giỏ hàng trống hoặc không có sản phẩm để áp dụng mã giảm giá.", 400);

        if (!cart.CanCheckout)
            throw new VoucherFailure("VALIDATION_ERROR", "Một hoặc nhiều sản phẩm trong giỏ hàng không khả dụng.", 400);

        if (!CodeRegex().IsMatch(raw))
            throw new VoucherFailure("VOUCHER_NOT_FOUND", "Mã giảm giá không tồn tại.", 404);

        var voucher = await db.Vouchers.AsNoTracking().FirstOrDefaultAsync(v => v.Code == raw, ct);
        if (voucher is null || voucher.VoucherStatus == "DRAFT")
            throw new VoucherFailure("VOUCHER_NOT_FOUND", "Mã giảm giá không tồn tại.", 404);

        if (voucher.VoucherStatus == "PAUSED")
            throw new VoucherFailure("VOUCHER_NOT_ACTIVE", "Mã giảm giá hiện đang tạm dừng áp dụng.", 409);

        if (voucher.VoucherStatus == "ENDED")
            throw new VoucherFailure("VOUCHER_EXPIRED", "Mã giảm giá đã kết thúc.", 409);

        if (voucher.VoucherStatus != "ACTIVE")
            throw new VoucherFailure("VOUCHER_NOT_ACTIVE", "Mã giảm giá hiện không khả dụng.", 409);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (now < voucher.StartsAt)
            throw new VoucherFailure("VOUCHER_NOT_STARTED", "Mã giảm giá chưa đến thời gian áp dụng.", 409);

        if (now >= voucher.EndsAt)
            throw new VoucherFailure("VOUCHER_EXPIRED", "Mã giảm giá đã hết hạn sử dụng.", 409);

        var remainingUsage = voucher.TotalUsageLimit - (voucher.UsageHeld + voucher.UsageConsumed);
        if (remainingUsage <= 0)
            throw new VoucherFailure("VOUCHER_USAGE_LIMIT_REACHED", "Mã giảm giá đã hết lượt sử dụng.", 409);

        var remainingBudget = voucher.BudgetTotal - (voucher.BudgetHeld + voucher.BudgetConsumed);
        if (remainingBudget <= 0)
            throw new VoucherFailure("VOUCHER_BUDGET_EXHAUSTED", "Ngân sách của mã giảm giá đã hết.", 409);

        var customerUsed = await db.VoucherRedemptions.AsNoTracking()
            .CountAsync(r => r.VoucherId == voucher.Id && r.CustomerId == customerId && (r.RedemptionStatus == "HELD" || r.RedemptionStatus == "CONSUMED"), ct);

        if (customerUsed >= voucher.PerCustomerLimit)
            throw new VoucherFailure("VOUCHER_CUSTOMER_LIMIT_REACHED", $"Bạn đã sử dụng hết lượt cho phép của mã giảm giá này ({voucher.PerCustomerLimit} lần).", 409);

        var subtotal = cart.Subtotal;
        if (subtotal < voucher.MinimumOrderAmount)
            throw new VoucherFailure("VOUCHER_MINIMUM_NOT_MET", $"Đơn hàng tối thiểu phải từ {voucher.MinimumOrderAmount:N0} đ để sử dụng mã này.", 409);

        decimal discount;
        if (voucher.DiscountType == "FIXED_AMOUNT")
        {
            discount = voucher.DiscountValue;
        }
        else if (voucher.DiscountType == "PERCENTAGE")
        {
            var rawDiscount = Math.Floor(subtotal * voucher.DiscountValue / 100m);
            discount = Math.Min(rawDiscount, voucher.MaxDiscountAmount ?? rawDiscount);
        }
        else
        {
            throw new VoucherFailure("VALIDATION_ERROR", "Loại chiết khấu không hợp lệ.", 400);
        }

        if (discount <= 0)
            throw new VoucherFailure("VOUCHER_DISCOUNT_INVALID", "Chiết khấu của mã giảm giá không hợp lệ.", 409);

        if (discount >= subtotal)
            throw new VoucherFailure("VOUCHER_DISCOUNT_INVALID", "Mã giảm giá không thể tạo đơn hàng 0 đ.", 409);

        if (discount > remainingBudget)
            throw new VoucherFailure("VOUCHER_BUDGET_EXHAUSTED", "Ngân sách còn lại của mã giảm giá không đủ để áp dụng cho đơn hàng này.", 409);

        var totalAmount = subtotal - discount;
        return new VoucherPreviewDto(voucher.Code, subtotal, discount, totalAmount, voucher.EndsAt);
    }
}

