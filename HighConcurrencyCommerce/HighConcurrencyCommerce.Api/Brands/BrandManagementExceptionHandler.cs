using HighConcurrencyCommerce.Api.Auth;
using HighConcurrencyCommerce.Application.Brands;
using Microsoft.AspNetCore.Diagnostics;

namespace HighConcurrencyCommerce.Api.Brands;
public sealed class BrandManagementExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    { if (exception is not BrandManagementFailure failure) return false; await AuthProblems.Write(context, failure.Status, failure.Code, failure.Message); return true; }
}
