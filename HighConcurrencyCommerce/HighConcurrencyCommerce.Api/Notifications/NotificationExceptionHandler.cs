using HighConcurrencyCommerce.Api.Auth;
using HighConcurrencyCommerce.Application.Notifications;
using Microsoft.AspNetCore.Diagnostics;

namespace HighConcurrencyCommerce.Api.Notifications;

public sealed class NotificationExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not NotificationFailure failure) return false;
        await AuthProblems.Write(context, failure.Status, failure.Code, failure.Message);
        return true;
    }
}
