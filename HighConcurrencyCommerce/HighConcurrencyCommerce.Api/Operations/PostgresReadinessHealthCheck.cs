using HighConcurrencyCommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HighConcurrencyCommerce.Api.Operations;

public sealed class PostgresReadinessHealthCheck(
    IServiceScopeFactory scopeFactory,
    ILogger<PostgresReadinessHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy("PostgreSQL reachable.")
                : HealthCheckResult.Unhealthy("PostgreSQL unavailable.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "PostgreSQL readiness check failed");
            return HealthCheckResult.Unhealthy("PostgreSQL unavailable.", exception);
        }
    }
}

