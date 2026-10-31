using HighConcurrencyCommerce.Application.FlashSale;

namespace HighConcurrencyCommerce.Api.FlashSale;

public sealed class FlashSaleLifecycleWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<FlashSaleLifecycleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(100,
            configuration.GetValue("FlashSaleLifecycle:PollIntervalMilliseconds", 15_000)));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<IFlashSaleService>();
                var changed = await service.SynchronizeLifecycleAsync(100, stoppingToken);
                if (changed > 0) logger.LogInformation("Synchronized {CampaignCount} Flash Sale campaign states", changed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Flash Sale lifecycle synchronization failed"); }
            await Task.Delay(interval, stoppingToken);
        }
    }
}
