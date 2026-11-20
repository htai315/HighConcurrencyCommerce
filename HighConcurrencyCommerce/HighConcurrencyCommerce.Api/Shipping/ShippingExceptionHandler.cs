using HighConcurrencyCommerce.Api.Auth;
using HighConcurrencyCommerce.Application.Shipping;
using Microsoft.AspNetCore.Diagnostics;

namespace HighConcurrencyCommerce.Api.Shipping;

public sealed class ShippingExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not ShippingFailure failure) return false;
        await AuthProblems.Write(context, failure.Status, failure.Code, failure.Message);
        return true;
    }
}
