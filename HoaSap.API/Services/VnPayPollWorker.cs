using HoaSap.BLL.Workshops;

namespace HoaSap.API.Services;

public sealed class VnPayPollWorker(IServiceScopeFactory scopeFactory, ILogger<VnPayPollWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var client = scope.ServiceProvider.GetRequiredService<VnPayClient>();
                if (!client.Enabled) continue;
                await scope.ServiceProvider.GetRequiredService<VnPayBookingService>().PollAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "VNPAY poll cycle failed; will retry"); }
        }
    }
}
