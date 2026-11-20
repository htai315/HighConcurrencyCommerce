using HighConcurrencyCommerce.Api.Auth;
using HighConcurrencyCommerce.Application.Payments;
using Microsoft.AspNetCore.Diagnostics;

namespace HighConcurrencyCommerce.Api.Payments;

public sealed class PayOsPaymentExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is PayOsPaymentFailure failure)
        {
            if (failure.RetryAfterSeconds is int seconds)
                context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await AuthProblems.Write(context, failure.Status, failure.Code, failure.Message);
            return true;
        }
        if (exception is PayOsReconciliationFailure reconciliation)
        {
            await AuthProblems.Write(context, reconciliation.Status, reconciliation.Code, reconciliation.Message);
            return true;
        }
        return false;
    }
}

