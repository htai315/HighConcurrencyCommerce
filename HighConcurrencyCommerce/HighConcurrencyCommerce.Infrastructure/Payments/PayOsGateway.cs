using HighConcurrencyCommerce.Application.Payments;
using Microsoft.Extensions.Options;
using PayOS;
using PayOS.Exceptions;
using PayOS.Models;
using PayOS.Models.V2.PaymentRequests;
using PayOS.Models.Webhooks;

namespace HighConcurrencyCommerce.Infrastructure.Payments;

public sealed class PayOsGateway : IPayOsGateway, IDisposable
{
    private readonly PayOsOptions options;
    private PayOSClient? client;
    private readonly int timeoutMs;

    public PayOsGateway(IOptions<PayOsOptions> options)
    {
        this.options = options.Value;
        timeoutMs = checked(this.options.RequestTimeoutSeconds * 1000);
    }

    public async Task<PayOsProviderPayment> CreatePaymentLinkAsync(
        PayOsCreateLinkCommand command,
        CancellationToken ct = default)
    {
        try
        {
            var response = await Client.PaymentRequests.CreateAsync(new CreatePaymentLinkRequest
            {
                OrderCode = command.OrderCode,
                Amount = command.Amount,
                Description = command.Description,
                ReturnUrl = command.ReturnUrl,
                CancelUrl = command.CancelUrl,
                ExpiredAt = command.ExpiresAtUnixSeconds
            }, new RequestOptions<CreatePaymentLinkRequest>
            {
                MaxRetries = 0,
                TimeoutMs = timeoutMs,
                CancellationToken = ct
            });

            return new PayOsProviderPayment(
                response.OrderCode,
                response.Amount,
                0,
                response.Amount,
                response.Status.ToString().ToUpperInvariant(),
                response.PaymentLinkId,
                response.CheckoutUrl,
                response.ExpiredAt,
                null,
                null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw MapFailure(ex);
        }
    }

    public async Task<PayOsProviderPayment> GetPaymentAsync(long orderCode, CancellationToken ct = default)
    {
        try
        {
            var response = await Client.PaymentRequests.GetAsync(orderCode, RequestOptions(ct));
            return Map(response);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw MapFailure(ex);
        }
    }

    public async Task<PayOsProviderPayment> CancelPaymentAsync(
        long orderCode,
        string reason,
        CancellationToken ct = default)
    {
        try
        {
            var response = await Client.PaymentRequests.CancelAsync(orderCode, reason,
                new RequestOptions<CancelPaymentLinkRequest>
                {
                    MaxRetries = 0,
                    TimeoutMs = timeoutMs,
                    CancellationToken = ct
                });
            return Map(response);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw MapFailure(ex);
        }
    }

    public void Dispose() => client?.Dispose();

    public async Task<PayOsVerifiedWebhook> VerifyWebhookAsync(
        PayOsWebhookEnvelope webhook,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var verified = await Client.Webhooks.VerifyAsync(new Webhook
            {
                Code = webhook.Code,
                Description = webhook.Description,
                Success = webhook.Success,
                Signature = webhook.Signature,
                Data = new WebhookData
                {
                    OrderCode = webhook.Data.OrderCode,
                    Amount = webhook.Data.Amount,
                    Description = webhook.Data.Description,
                    AccountNumber = webhook.Data.AccountNumber,
                    Reference = webhook.Data.Reference,
                    TransactionDateTime = webhook.Data.TransactionDateTime,
                    Currency = webhook.Data.Currency,
                    PaymentLinkId = webhook.Data.PaymentLinkId,
                    Code = webhook.Data.Code,
                    Description2 = webhook.Data.Description2,
                    CounterAccountBankId = webhook.Data.CounterAccountBankId,
                    CounterAccountBankName = webhook.Data.CounterAccountBankName,
                    CounterAccountName = webhook.Data.CounterAccountName,
                    CounterAccountNumber = webhook.Data.CounterAccountNumber,
                    VirtualAccountName = webhook.Data.VirtualAccountName,
                    VirtualAccountNumber = webhook.Data.VirtualAccountNumber
                }
            });

            return new PayOsVerifiedWebhook(
                verified.OrderCode,
                verified.Amount,
                verified.Reference,
                verified.TransactionDateTime,
                verified.Currency,
                verified.PaymentLinkId,
                verified.Code,
                verified.Description2);
        }
        catch (InvalidSignatureException ex)
        {
            throw new PayOsGatewayFailure(
                "PAYOS_INVALID_SIGNATURE",
                "Chữ ký webhook PayOS không hợp lệ.",
                false,
                ex);
        }
        catch (Exception ex)
        {
            throw new PayOsGatewayFailure(
                "PAYOS_WEBHOOK_VERIFICATION_UNAVAILABLE",
                "Không thể xác minh webhook PayOS.",
                true,
                ex);
        }
    }

    private PayOSClient Client => client ??= new PayOSClient(new PayOSOptions
    {
        ClientId = options.ClientId,
        ApiKey = options.ApiKey,
        ChecksumKey = options.ChecksumKey,
        TimeoutMs = timeoutMs,
        MaxRetries = 0
    });

    private RequestOptions RequestOptions(CancellationToken ct) => new()
    {
        MaxRetries = 0,
        TimeoutMs = timeoutMs,
        CancellationToken = ct
    };

    private static PayOsProviderPayment Map(PaymentLink response) => new(
        response.OrderCode,
        response.Amount,
        response.AmountPaid,
        response.AmountRemaining,
        response.Status.ToString().ToUpperInvariant(),
        response.Id,
        null,
        null,
        response.CancellationReason,
        ParseProviderDate(response.CanceledAt));

    private static DateTime? ParseProviderDate(string? value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime
            : null;

    private static PayOsGatewayFailure MapFailure(Exception ex)
    {
        if (ex is ApiException api)
        {
            var status = api.StatusCode;
            var ambiguous = status is null or 408 or 429 || status >= 500;
            return new PayOsGatewayFailure(
                SafeCode(api.ErrorCode, status),
                ambiguous ? "Không thể xác định kết quả từ PayOS." : "PayOS từ chối yêu cầu.",
                ambiguous,
                ex);
        }

        return new PayOsGatewayFailure(
            ex.GetType().Name.Length <= 64 ? ex.GetType().Name : "PAYOS_GATEWAY_ERROR",
            "Không thể kết nối PayOS.",
            true,
            ex);
    }

    private static string SafeCode(string? providerCode, int? status)
    {
        var normalized = string.IsNullOrWhiteSpace(providerCode)
            ? $"HTTP_{status?.ToString() ?? "UNKNOWN"}"
            : new string(providerCode.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray());
        return normalized.Length switch
        {
            0 => "PAYOS_ERROR",
            > 64 => normalized[..64],
            _ => normalized
        };
    }
}

