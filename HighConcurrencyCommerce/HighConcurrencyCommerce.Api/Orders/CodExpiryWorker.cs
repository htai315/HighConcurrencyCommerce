using HighConcurrencyCommerce.Application.Orders;

namespace HighConcurrencyCommerce.Api.Orders;

public sealed class CodExpiryWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<CodExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(100,
            configuration.GetValue("CodExpiry:PollIntervalMilliseconds", 30_000)));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<ICodOrderService>();
                var count = await service.ExpireDueAsync(100, stoppingToken);
                if (count > 0) logger.LogInformation("Expired {Count} overdue COD orders.", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "COD expiry worker iteration failed.");
            }
            await Task.Delay(interval, stoppingToken);
        }
    }
}
