using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HighConcurrencyCommerce.Api.Operations;

public static class HealthResponseWriter
{
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 2),
            checks = report.Entries.OrderBy(x => x.Key).Select(x => new
            {
                name = x.Key,
                status = x.Value.Status.ToString(),
                durationMs = Math.Round(x.Value.Duration.TotalMilliseconds, 2)
            })
        });
    }
}

