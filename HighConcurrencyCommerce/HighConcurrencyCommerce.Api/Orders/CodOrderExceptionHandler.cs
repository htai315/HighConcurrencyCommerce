using HighConcurrencyCommerce.Api.Auth;
using HighConcurrencyCommerce.Application.Orders;
using Microsoft.AspNetCore.Diagnostics;

namespace HighConcurrencyCommerce.Api.Orders;

public sealed class CodOrderExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not CodOrderFailure failure) return false;
        await AuthProblems.Write(context, failure.Status, failure.Code, failure.Message);
        return true;
    }
}
