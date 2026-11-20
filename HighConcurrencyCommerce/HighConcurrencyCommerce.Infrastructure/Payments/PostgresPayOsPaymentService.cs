using HighConcurrencyCommerce.Application.Payments;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HighConcurrencyCommerce.Infrastructure.Payments;

public sealed class PostgresPayOsPaymentService(
    StoreDbContext db,
    IPayOsGateway gateway,
    IOptions<PayOsOptions> options,
    TimeProvider timeProvider) : IPayOsPaymentService
{
    private readonly PayOsOptions options = options.Value;

    public async Task<PayOsAttemptExecution> CreateOrResumeAttemptAsync(
        Guid customerId,
        Guid orderId,
        CancellationToken ct = default)
    {
        EnsureEnabled();
        var prepared = await PrepareAsync(customerId, orderId, ct);
        if (prepared.Replay is not null)
            return new PayOsAttemptExecution(prepared.Replay, true);
        if (prepared.WaitOnly)
            throw new PayOsPaymentFailure(
                "PAYMENT_ATTEMPT_IN_PROGRESS",
                "PayOS đang xử lý yêu cầu tạo link. Vui lòng kiểm tra lại sau.",
                409,
                options.AttemptResolutionDelaySeconds);

        PayOsProviderPayment provider;
        try
        {
            if (prepared.ResolveOnly)
            {
                provider = await gateway.GetPaymentAsync(prepared.OrderCode, ct);
                await MarkQueriedAsync(prepared.AttemptId, null, ct);
                throw new PayOsPaymentFailure(
                    "PAYMENT_STATE_UNRESOLVED",
                    $"PayOS đã ghi nhận yêu cầu ở trạng thái {provider.Status}, nhưng link thanh toán cần được tạo lại có kiểm soát.",
                    409,
                    3);
            }

            provider = await gateway.CreatePaymentLinkAsync(new PayOsCreateLinkCommand(
                prepared.OrderCode,
                ToProviderAmount(prepared.ExpectedAmount),
                BuildDescription(prepared.OrderNumber),
                options.ReturnUrl,
                options.CancelUrl,
                new DateTimeOffset(DateTime.SpecifyKind(prepared.ExpiresAt, DateTimeKind.Utc)).ToUnixTimeSeconds()), ct);
        }
        catch (PayOsGatewayFailure failure)
        {
            await MarkGatewayFailureAsync(prepared.AttemptId, failure, ct);
            throw new PayOsPaymentFailure(
                failure.IsAmbiguous ? "PAYMENT_STATE_UNRESOLVED" : "PAYMENT_PROVIDER_REJECTED",
                failure.IsAmbiguous
                    ? "Chưa thể xác định PayOS đã tạo link hay chưa. Vui lòng thử kiểm tra lại, không tạo đơn mới."
                    : "PayOS từ chối tạo link thanh toán.",
                failure.IsAmbiguous ? 503 : 409,
                failure.IsAmbiguous ? 3 : null);
        }

        ValidateProviderResponse(prepared, provider);
        var attempt = await PersistLinkAsync(prepared, provider, ct);
        return new PayOsAttemptExecution(attempt, false);
    }

    public async Task<PayOsPaymentStatusDto> GetPaymentStatusAsync(
        Guid customerId,
        Guid orderId,
        CancellationToken ct = default)
    {
        var order = await db.SalesOrders.AsNoTracking()
            .Where(x => x.Id == orderId && x.CustomerId == customerId && x.PaymentMethod == "PAYOS")
            .Select(x => new
            {
                x.Id,
                x.OrderNumber,
                x.OrderStatus,
                x.DeadlineAt,
                PaymentStatus = x.Payment!.PaymentStatus,
                x.Payment.ExpectedAmount,
                Attempt = x.Payment.PaymentAttempts.OrderByDescending(a => a.AttemptNo).Select(a => new
                {
                    a.Id,
                    a.AttemptNo,
                    a.AttemptStatus,
                    a.ProviderLink,
                    a.ExpiresAt
                }).FirstOrDefault()
            })
            .SingleOrDefaultAsync(ct)
            ?? throw NotFound();

        var attempt = order.Attempt is null ? null : new PayOsAttemptDto(
            order.Attempt.Id,
            order.Attempt.AttemptNo,
            order.Attempt.AttemptStatus,
            SafeCheckoutUrl(order.Attempt.ProviderLink),
            order.Attempt.ExpiresAt,
            order.Attempt.AttemptStatus == "CREATED");

        return new PayOsPaymentStatusDto(
            order.Id,
            order.OrderNumber,
            order.OrderStatus,
            order.PaymentStatus,
            order.ExpectedAmount,
            order.DeadlineAt,
            attempt);
    }

    private async Task<PreparedAttempt> PrepareAsync(Guid customerId, Guid orderId, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await db.SalesOrders
            .FromSqlInterpolated($"SELECT * FROM store.sales_orders WHERE id={orderId} AND customer_id={customerId} FOR UPDATE")
            .SingleOrDefaultAsync(ct)
            ?? throw NotFound();
        var payment = await db.Payments
            .FromSqlInterpolated($"SELECT * FROM store.payments WHERE order_id={orderId} FOR UPDATE")
            .SingleOrDefaultAsync(ct)
            ?? throw NotFound();

        ValidatePending(order, payment, now);
        var active = await db.PaymentAttempts
            .Where(x => x.PaymentId == payment.Id && (x.AttemptStatus == "CREATED" || x.AttemptStatus == "LINK_READY"))
            .OrderByDescending(x => x.AttemptNo)
            .FirstOrDefaultAsync(ct);

        if (active is not null)
        {
            await tx.CommitAsync(ct);
            if (active.AttemptStatus == "LINK_READY")
            {
                var replay = new PayOsAttemptDto(active.Id, active.AttemptNo, active.AttemptStatus,
                    SafeCheckoutUrl(active.ProviderLink), active.ExpiresAt, false);
                return new PreparedAttempt(active.Id, active.ProviderOrderCode!.Value, active.ExpiresAt,
                    order.OrderNumber, payment.ExpectedAmount, false, false, replay);
            }

            var waitOnly = active.CreatedAt > now.AddSeconds(-options.AttemptResolutionDelaySeconds);
            return new PreparedAttempt(active.Id, active.ProviderOrderCode!.Value, active.ExpiresAt,
                order.OrderNumber, payment.ExpectedAmount, !waitOnly, waitOnly, null);
        }

        var latestAttempt = await db.PaymentAttempts
            .Where(x => x.PaymentId == payment.Id)
            .MaxAsync(x => (short?)x.AttemptNo, ct) ?? 0;
        if (latestAttempt >= 3)
            throw new PayOsPaymentFailure("PAYMENT_ATTEMPT_LIMIT_REACHED", "Đơn hàng đã đạt giới hạn ba lần tạo link thanh toán.", 409);

        var orderCode = await db.Database.SqlQuery<long>(
            $"SELECT nextval('store.payos_order_code_seq') AS \"Value\"").SingleAsync(ct);
        var attempt = new PaymentAttempt
        {
            Id = Guid.NewGuid(),
            PaymentId = payment.Id,
            AttemptNo = checked((short)(latestAttempt + 1)),
            ProviderOrderCode = orderCode,
            ExpiresAt = order.DeadlineAt,
            AttemptStatus = "CREATED",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.PaymentAttempts.Add(attempt);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new PreparedAttempt(attempt.Id, orderCode, attempt.ExpiresAt,
            order.OrderNumber, payment.ExpectedAmount, false, false, null);
    }

    private async Task<PayOsAttemptDto> PersistLinkAsync(
        PreparedAttempt prepared,
        PayOsProviderPayment provider,
        CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var attempt = await db.PaymentAttempts
            .FromSqlInterpolated($"SELECT * FROM store.payment_attempts WHERE id={prepared.AttemptId} FOR UPDATE")
            .SingleAsync(ct);

        if (attempt.AttemptStatus == "LINK_READY")
        {
            await tx.CommitAsync(ct);
            return new PayOsAttemptDto(attempt.Id, attempt.AttemptNo, attempt.AttemptStatus,
                SafeCheckoutUrl(attempt.ProviderLink), attempt.ExpiresAt, false);
        }
        if (attempt.AttemptStatus != "CREATED")
            throw new PayOsPaymentFailure("PAYMENT_STATE_CHANGED", "Trạng thái thanh toán đã thay đổi.", 409);

        attempt.ProviderPaymentLinkId = provider.PaymentLinkId;
        attempt.ProviderLink = ValidateCheckoutUrl(provider.CheckoutUrl);
        attempt.AttemptStatus = "LINK_READY";
        attempt.LastErrorCode = null;
        attempt.LastQueriedAt = now;
        attempt.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new PayOsAttemptDto(attempt.Id, attempt.AttemptNo, attempt.AttemptStatus,
            attempt.ProviderLink, attempt.ExpiresAt, false);
    }

    private async Task MarkGatewayFailureAsync(Guid attemptId, PayOsGatewayFailure failure, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var attempt = await db.PaymentAttempts
            .FromSqlInterpolated($"SELECT * FROM store.payment_attempts WHERE id={attemptId} FOR UPDATE")
            .SingleAsync(ct);
        if (attempt.AttemptStatus == "CREATED")
        {
            attempt.LastErrorCode = failure.Code;
            attempt.LastQueriedAt = now;
            attempt.UpdatedAt = now;
            if (!failure.IsAmbiguous) attempt.AttemptStatus = "FAILED";
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private async Task MarkQueriedAsync(Guid attemptId, string? errorCode, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await db.PaymentAttempts.Where(x => x.Id == attemptId).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.LastQueriedAt, timeProvider.GetUtcNow().UtcDateTime)
            .SetProperty(x => x.LastErrorCode, errorCode)
            .SetProperty(x => x.UpdatedAt, timeProvider.GetUtcNow().UtcDateTime), ct);
    }

    private void ValidateProviderResponse(PreparedAttempt prepared, PayOsProviderPayment provider)
    {
        if (provider.OrderCode != prepared.OrderCode || provider.Amount != ToProviderAmount(prepared.ExpectedAmount))
            throw new PayOsPaymentFailure("PAYMENT_PROVIDER_MISMATCH", "PayOS trả về dữ liệu không khớp với đơn hàng.", 502);
        if (provider.Status != "PENDING")
            throw new PayOsPaymentFailure("PAYMENT_STATE_CHANGED", $"PayOS trả về trạng thái {provider.Status}.", 409);
        if (string.IsNullOrWhiteSpace(provider.PaymentLinkId))
            throw new PayOsPaymentFailure("PAYMENT_PROVIDER_INVALID_RESPONSE", "PayOS không trả về mã payment link.", 502);
    }

    private void ValidatePending(SalesOrder order, Payment payment, DateTime now)
    {
        if (order.PaymentMethod != "PAYOS" || payment.PaymentMethod != "PAYOS") throw NotFound();
        if (order.OrderStatus != "PENDING_PAYMENT" || payment.PaymentStatus != "PENDING")
            throw new PayOsPaymentFailure("PAYMENT_NOT_PENDING", "Đơn hàng không còn chờ thanh toán PayOS.", 409);
        if (order.DeadlineAt <= now)
            throw new PayOsPaymentFailure("PAYMENT_EXPIRED", "Thời hạn thanh toán của đơn hàng đã hết.", 409);
    }

    private void EnsureEnabled()
    {
        if (!options.Enabled)
            throw new PayOsPaymentFailure("PAYOS_DISABLED", "PayOS chưa được bật cho môi trường này.", 409);
    }

    private string ValidateCheckoutUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !options.CheckoutUrlAllowedHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
            throw new PayOsPaymentFailure("PAYMENT_LINK_INVALID", "PayOS trả về đường dẫn thanh toán không hợp lệ.", 502);
        return uri.AbsoluteUri;
    }

    private string? SafeCheckoutUrl(string? value) => string.IsNullOrWhiteSpace(value) ? null : ValidateCheckoutUrl(value);

    private static long ToProviderAmount(decimal amount)
    {
        if (amount <= 0 || amount != decimal.Truncate(amount) || amount > long.MaxValue)
            throw new PayOsPaymentFailure("PAYMENT_AMOUNT_INVALID", "Số tiền thanh toán không hợp lệ.", 409);
        return decimal.ToInt64(amount);
    }

    private static string BuildDescription(string orderNumber)
    {
        var normalized = new string(orderNumber.Where(char.IsLetterOrDigit).ToArray());
        var value = $"HCC {normalized}";
        return value.Length <= 25 ? value : value[..25];
    }

    private static PayOsPaymentFailure NotFound() =>
        new("PAYMENT_NOT_FOUND", "Không tìm thấy thanh toán.", 404);

    private sealed record PreparedAttempt(
        Guid AttemptId,
        long OrderCode,
        DateTime ExpiresAt,
        string OrderNumber,
        decimal ExpectedAmount,
        bool ResolveOnly,
        bool WaitOnly,
        PayOsAttemptDto? Replay);
}

