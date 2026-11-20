using HighConcurrencyCommerce.Api.Auth;
using HighConcurrencyCommerce.Application.ProductRevisions;
using Microsoft.AspNetCore.Diagnostics;

namespace HighConcurrencyCommerce.Api.ProductRevisions;

public sealed class ProductRevisionExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not ProductRevisionFailure failure) return false;
        var status = failure.Code switch
        {
            "REVISION_NOT_FOUND" => 404,
            "REVISION_ALREADY_OPEN" or "REVISION_INVALID_STATE" or "REVISION_STALE" or "REVISION_BRAND_INACTIVE" or "REVISION_SNAPSHOT_INVALID" => 409,
            _ => 400
        };
        await AuthProblems.Write(context, status, failure.Code, failure.Message);
        return true;
    }
}
