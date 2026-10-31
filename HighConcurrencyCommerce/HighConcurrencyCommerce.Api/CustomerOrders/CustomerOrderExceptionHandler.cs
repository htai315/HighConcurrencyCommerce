using HighConcurrencyCommerce.Api.Auth;
using HighConcurrencyCommerce.Application.CustomerOrders;
using Microsoft.AspNetCore.Diagnostics;

namespace HighConcurrencyCommerce.Api.CustomerOrders;

public sealed class CustomerOrderExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not CustomerOrderFailure failure) return false;
        await AuthProblems.Write(context, failure.Status, failure.Code, failure.Message);
        return true;
    }
}
