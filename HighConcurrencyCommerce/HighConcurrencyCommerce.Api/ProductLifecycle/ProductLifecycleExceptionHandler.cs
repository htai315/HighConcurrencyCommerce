using HighConcurrencyCommerce.Api.Auth;
using HighConcurrencyCommerce.Application.ProductLifecycle;
using Microsoft.AspNetCore.Diagnostics;

namespace HighConcurrencyCommerce.Api.ProductLifecycle;

public sealed class ProductLifecycleExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not ProductLifecycleFailure failure) return false;
        await AuthProblems.Write(context, failure.HttpStatus, failure.Code, failure.Message);
        return true;
    }
}
