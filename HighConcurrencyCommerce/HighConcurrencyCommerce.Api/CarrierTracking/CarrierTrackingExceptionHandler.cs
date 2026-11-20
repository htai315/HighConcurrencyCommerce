using HighConcurrencyCommerce.Api.Auth;
using HighConcurrencyCommerce.Application.CarrierTracking;
using Microsoft.AspNetCore.Diagnostics;

namespace HighConcurrencyCommerce.Api.CarrierTracking;

public sealed class CarrierTrackingExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not CarrierTrackingFailure failure) return false;
        await AuthProblems.Write(context, failure.Status, failure.Code, failure.Message);
        return true;
    }
}
