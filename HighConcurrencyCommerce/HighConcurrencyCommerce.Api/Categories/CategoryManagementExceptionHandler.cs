using HighConcurrencyCommerce.Api.Auth;
using HighConcurrencyCommerce.Application.Categories;
using Microsoft.AspNetCore.Diagnostics;

namespace HighConcurrencyCommerce.Api.Categories;

public sealed class CategoryManagementExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not CategoryManagementFailure failure) return false;
        await AuthProblems.Write(context, failure.Status, failure.Code, failure.Message);
        return true;
    }
}
