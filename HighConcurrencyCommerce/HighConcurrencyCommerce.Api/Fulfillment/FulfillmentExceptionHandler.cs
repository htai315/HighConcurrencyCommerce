using HighConcurrencyCommerce.Api.Auth;
using HighConcurrencyCommerce.Application.Fulfillment;
using Microsoft.AspNetCore.Diagnostics;

namespace HighConcurrencyCommerce.Api.Fulfillment;

public sealed class FulfillmentExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not FulfillmentFailure failure) return false;
        await AuthProblems.Write(context, failure.Status, failure.Code, failure.Message);
        return true;
    }
}
