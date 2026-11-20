using HighConcurrencyCommerce.Application.Payments;

namespace HighConcurrencyCommerce.Api.Payments;

public sealed class PayOsExpiryWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<PayOsExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(1_000,
            configuration.GetValue("PayOs:ExpiryPollIntervalMilliseconds", 30_000)));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<IPayOsLifecycleService>();
                var count = await service.ExpireDueAsync(100, stoppingToken);
                if (count > 0) logger.LogInformation("Expired {Count} overdue PayOS orders.", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "PayOS expiry worker iteration failed.");
            }
            await Task.Delay(interval, stoppingToken);
        }
    }
}
