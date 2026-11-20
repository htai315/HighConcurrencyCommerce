using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HighConcurrencyCommerce.Application.Payments;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HighConcurrencyCommerce.Infrastructure.Payments;

public sealed class PostgresPayOsWebhookService(
    StoreDbContext db,
    IPayOsGateway gateway,
    IPayOsPaymentTransitionService transitions,
    TimeProvider timeProvider) : IPayOsWebhookService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PayOsWebhookReceipt> ReceiveAsync(
        string rawPayload,
        PayOsWebhookEnvelope webhook,
        CancellationToken ct = default)
    {
        if (Encoding.UTF8.GetByteCount(rawPayload) > 64 * 1024)
            throw new PayOsPaymentFailure("PAYOS_WEBHOOK_TOO_LARGE", "Webhook vượt quá kích thước cho phép.", 413);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawPayload))).ToLowerInvariant();
        var existing = await db.ProviderCallbackEvents.AsNoTracking()
            .Where(x => x.PayloadHash == hash)
            .Select(x => new { x.Id, x.SignatureValid, x.ProcessResult, x.Payload })
            .SingleOrDefaultAsync(ct);
        if (existing is not null)
        {
            if (existing.SignatureValid && existing.ProcessResult == "RECEIVED")
            {
                var replay = JsonSerializer.Deserialize<SafeWebhookPayload>(existing.Payload, JsonOptions);
                if (replay is not null)
                    await transitions.ApplyVerifiedWebhookAsync(existing.Id, replay.ToVerified(), ct);
            }
            return new PayOsWebhookReceipt(true, existing.SignatureValid);
        }

        PayOsVerifiedWebhook? verified = null;
        var signatureValid = true;
        try
        {
            verified = await gateway.VerifyWebhookAsync(webhook, ct);
        }
        catch (PayOsGatewayFailure failure) when (failure.Code == "PAYOS_INVALID_SIGNATURE")
        {
            signatureValid = false;
        }
        catch (PayOsGatewayFailure failure)
        {
            throw new PayOsPaymentFailure(
                "PAYOS_WEBHOOK_VERIFICATION_UNAVAILABLE",
                "Tạm thời chưa thể xác minh webhook PayOS.",
                503,
                failure.IsAmbiguous ? 3 : null);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var orderCode = verified?.OrderCode ?? webhook.Data.OrderCode;
        var successfulPayment = signatureValid
            && webhook.Success
            && string.Equals(webhook.Code, "00", StringComparison.Ordinal)
            && string.Equals(verified?.Code, "00", StringComparison.Ordinal);
        var attemptId = await db.PaymentAttempts.AsNoTracking()
            .Where(x => x.ProviderOrderCode == orderCode)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(ct);
        var safePayload = JsonSerializer.Serialize(new
        {
            orderCode,
            amount = verified?.Amount ?? webhook.Data.Amount,
            reference = verified?.Reference ?? webhook.Data.Reference,
            transactionDateTime = verified?.TransactionDateTime ?? webhook.Data.TransactionDateTime,
            currency = verified?.Currency ?? webhook.Data.Currency,
            paymentLinkId = verified?.PaymentLinkId ?? webhook.Data.PaymentLinkId,
            code = verified?.Code ?? webhook.Data.Code,
            description = verified?.Description ?? webhook.Data.Description2
        }, JsonOptions);

        var callbackEvent = new ProviderCallbackEvent
        {
            Id = Guid.NewGuid(),
            AttemptId = attemptId,
            ProviderOrderCode = orderCode,
            PayloadHash = hash,
            Payload = safePayload,
            SignatureValid = signatureValid,
            ReceivedAt = now,
            ProcessResult = successfulPayment ? "RECEIVED" : "REJECTED",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.ProviderCallbackEvents.Add(callbackEvent);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                   { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_provider_callback_payload_hash" })
        {
            db.ChangeTracker.Clear();
            var raced = await db.ProviderCallbackEvents.AsNoTracking()
                .SingleAsync(x => x.PayloadHash == hash, ct);
            if (raced.SignatureValid && raced.ProcessResult == "RECEIVED" && verified is not null)
                await transitions.ApplyVerifiedWebhookAsync(raced.Id, verified, ct);
            return new PayOsWebhookReceipt(true, raced.SignatureValid);
        }

        if (!signatureValid)
            throw new PayOsPaymentFailure("PAYOS_INVALID_SIGNATURE", "Chữ ký webhook PayOS không hợp lệ.", 400);

        if (successfulPayment)
            await transitions.ApplyVerifiedWebhookAsync(callbackEvent.Id, verified!, ct);

        return new PayOsWebhookReceipt(false, true);
    }

    private sealed record SafeWebhookPayload(
        long OrderCode,
        long Amount,
        string Reference,
        string TransactionDateTime,
        string Currency,
        string PaymentLinkId,
        string Code,
        string Description)
    {
        public PayOsVerifiedWebhook ToVerified() =>
            new(OrderCode, Amount, Reference, TransactionDateTime, Currency, PaymentLinkId, Code, Description);
    }
}

