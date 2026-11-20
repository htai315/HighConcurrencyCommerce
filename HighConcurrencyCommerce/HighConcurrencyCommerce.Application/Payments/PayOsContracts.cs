using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations;

namespace HighConcurrencyCommerce.Application.Payments;

public sealed record PayOsCreateLinkCommand(
    long OrderCode,
    long Amount,
    string Description,
    string ReturnUrl,
    string CancelUrl,
    long ExpiresAtUnixSeconds);

public sealed record PayOsProviderPayment(
    long OrderCode,
    long Amount,
    long AmountPaid,
    long AmountRemaining,
    string Status,
    string PaymentLinkId,
    string? CheckoutUrl,
    long? ExpiresAtUnixSeconds,
    string? CancellationReason,
    DateTime? CancelledAt);

public sealed class PayOsGatewayFailure(
    string code,
    string message,
    bool isAmbiguous,
    Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
    public bool IsAmbiguous { get; } = isAmbiguous;
}

public interface IPayOsGateway
{
    Task<PayOsProviderPayment> CreatePaymentLinkAsync(PayOsCreateLinkCommand command, CancellationToken ct = default);
    Task<PayOsProviderPayment> GetPaymentAsync(long orderCode, CancellationToken ct = default);
    Task<PayOsProviderPayment> CancelPaymentAsync(long orderCode, string reason, CancellationToken ct = default);
    Task<PayOsVerifiedWebhook> VerifyWebhookAsync(PayOsWebhookEnvelope webhook, CancellationToken ct = default);
}

public sealed record PayOsWebhookData(
    long OrderCode,
    long Amount,
    string Description,
    string AccountNumber,
    string Reference,
    string TransactionDateTime,
    string Currency,
    string PaymentLinkId,
    string Code,
    [property: JsonPropertyName("desc")] string Description2,
    string CounterAccountBankId,
    string CounterAccountBankName,
    string CounterAccountName,
    string CounterAccountNumber,
    string VirtualAccountName,
    string VirtualAccountNumber);

public sealed record PayOsWebhookEnvelope(
    string Code,
    [property: JsonPropertyName("desc")] string Description,
    bool Success,
    PayOsWebhookData Data,
    string Signature);

public sealed record PayOsVerifiedWebhook(
    long OrderCode,
    long Amount,
    string Reference,
    string TransactionDateTime,
    string Currency,
    string PaymentLinkId,
    string Code,
    string Description);

public sealed record PayOsWebhookReceipt(bool Duplicate, bool SignatureValid);

public interface IPayOsWebhookService
{
    Task<PayOsWebhookReceipt> ReceiveAsync(
        string rawPayload,
        PayOsWebhookEnvelope webhook,
        CancellationToken ct = default);
}

public interface IPayOsPaymentTransitionService
{
    Task ApplyVerifiedWebhookAsync(
        Guid callbackEventId,
        PayOsVerifiedWebhook webhook,
        CancellationToken ct = default);
}

public sealed record PayOsAttemptDto(
    Guid AttemptId,
    short AttemptNo,
    string Status,
    string? CheckoutUrl,
    DateTime ExpiresAt,
    bool RequiresProviderQuery);

public sealed record PayOsAttemptExecution(PayOsAttemptDto Attempt, bool IsReplay);

public sealed record PayOsPaymentStatusDto(
    Guid OrderId,
    string OrderNumber,
    string OrderStatus,
    string PaymentStatus,
    decimal ExpectedAmount,
    DateTime DeadlineAt,
    PayOsAttemptDto? LatestAttempt);

public sealed class PayOsPaymentFailure(
    string code,
    string message,
    int status = 400,
    int? retryAfterSeconds = null) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

public interface IPayOsPaymentService
{
    Task<PayOsAttemptExecution> CreateOrResumeAttemptAsync(
        Guid customerId,
        Guid orderId,
        CancellationToken ct = default);

    Task<PayOsPaymentStatusDto> GetPaymentStatusAsync(
        Guid customerId,
        Guid orderId,
        CancellationToken ct = default);
}

public sealed class CancelPayOsPaymentRequest
{
    [Required, StringLength(300, MinimumLength = 3)]
    public string Reason { get; init; } = string.Empty;
}

public interface IPayOsLifecycleService
{
    Task CancelAsync(Guid customerId, Guid orderId, string reason, CancellationToken ct = default);
    Task<int> ExpireDueAsync(int batchSize = 100, CancellationToken ct = default);
}

