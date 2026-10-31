using HighConcurrencyCommerce.Api.Auth;
using HighConcurrencyCommerce.Application.Checkout;
using Microsoft.AspNetCore.Diagnostics;

namespace HighConcurrencyCommerce.Api.Checkout;

public sealed class CheckoutExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not CheckoutFailure failure) return false;
        if (failure.IsReplay) context.Response.Headers["Idempotency-Replayed"] = "true";
        if (failure.Code == "CHECKOUT_IN_PROGRESS") context.Response.Headers.RetryAfter = "2";
        await AuthProblems.Write(context, failure.Status, failure.Code, failure.Message);
        return true;
    }
}
