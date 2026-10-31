using HighConcurrencyCommerce.Api.Auth;
using HighConcurrencyCommerce.Application.Reporting;
using Microsoft.AspNetCore.Diagnostics;

namespace HighConcurrencyCommerce.Api.Reporting;

public sealed class ReportingExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not ReportingFailure failure) return false;
        await AuthProblems.Write(context, failure.Status, failure.Code, failure.Message);
        return true;
    }
}
