using HighConcurrencyCommerce.Application.Vouchers;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HighConcurrencyCommerce.Infrastructure.Vouchers;

public sealed class PostgresVoucherLifecycleManager(StoreDbContext db) : IVoucherLifecycleManager
{
    public async Task ConsumeForCompletedOrderAsync(Guid orderId, DateTime now, CancellationToken ct = default)
    {
        var redemption = await db.VoucherRedemptions
            .SingleOrDefaultAsync(r => r.OrderId == orderId, ct);
        if (redemption is null)
        {
            return;
        }

        if (redemption.RedemptionStatus == "CONSUMED")
        {
            return;
        }

        if (redemption.RedemptionStatus != "HELD")
        {
            throw new InvalidOperationException($"Cannot consume voucher redemption {redemption.Id} in '{redemption.RedemptionStatus}' state.");
        }

        var voucher = await db.Vouchers
            .FromSqlInterpolated($"SELECT * FROM store.vouchers WHERE id = {redemption.VoucherId} FOR UPDATE")
            .SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Voucher {redemption.VoucherId} not found for redemption {redemption.Id}.");

        redemption.RedemptionStatus = "CONSUMED";
        redemption.ConsumedAt = now;
        redemption.UpdatedAt = now;

        voucher.UsageHeld = Math.Max(0, voucher.UsageHeld - 1);
        voucher.UsageConsumed += 1;
        voucher.BudgetHeld = Math.Max(0m, voucher.BudgetHeld - redemption.DiscountAmount);
        voucher.BudgetConsumed += redemption.DiscountAmount;
        voucher.UpdatedAt = now;
    }

    public async Task ReleaseForClosedOrderAsync(Guid orderId, string releaseReason, DateTime now, CancellationToken ct = default)
    {
        var redemption = await db.VoucherRedemptions
            .SingleOrDefaultAsync(r => r.OrderId == orderId, ct);
        if (redemption is null)
        {
            return;
        }

        if (redemption.RedemptionStatus == "RELEASED")
        {
            return;
        }

        if (redemption.RedemptionStatus != "HELD")
        {
            throw new InvalidOperationException($"Cannot release voucher redemption {redemption.Id} in '{redemption.RedemptionStatus}' state.");
        }

        var voucher = await db.Vouchers
            .FromSqlInterpolated($"SELECT * FROM store.vouchers WHERE id = {redemption.VoucherId} FOR UPDATE")
            .SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Voucher {redemption.VoucherId} not found for redemption {redemption.Id}.");

        redemption.RedemptionStatus = "RELEASED";
        redemption.ReleasedAt = now;
        redemption.ReleaseReason = releaseReason;
        redemption.UpdatedAt = now;

        voucher.UsageHeld = Math.Max(0, voucher.UsageHeld - 1);
        voucher.BudgetHeld = Math.Max(0m, voucher.BudgetHeld - redemption.DiscountAmount);
        voucher.UpdatedAt = now;
    }
}

