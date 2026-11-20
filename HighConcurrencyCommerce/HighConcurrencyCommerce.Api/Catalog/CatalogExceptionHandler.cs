using HighConcurrencyCommerce.Api.Auth;
using HighConcurrencyCommerce.Application.Catalog;
using Microsoft.AspNetCore.Diagnostics;

namespace HighConcurrencyCommerce.Api.Catalog;

public sealed class CatalogExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not ProductComparisonFailure failure) return false;

        var status = failure.Code switch
        {
            ProductComparisonErrorCodes.CategoryMismatch => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status500InternalServerError
        };
        await AuthProblems.Write(context, status, failure.Code, failure.Message);
        return true;
    }
}
