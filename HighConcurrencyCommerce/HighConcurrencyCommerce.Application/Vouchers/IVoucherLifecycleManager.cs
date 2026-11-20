namespace HighConcurrencyCommerce.Application.Vouchers;

public interface IVoucherLifecycleManager
{
    Task ConsumeForCompletedOrderAsync(Guid orderId, DateTime now, CancellationToken ct = default);
    Task ReleaseForClosedOrderAsync(Guid orderId, string releaseReason, DateTime now, CancellationToken ct = default);
}

