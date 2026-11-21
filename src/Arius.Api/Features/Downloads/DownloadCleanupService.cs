using Arius.Api.Shared.Hosting;


namespace Arius.Api.Features.Downloads;

/// <summary>
/// Hourly sweep that deletes download staging folders once their clean-up period has passed
/// (see <see cref="DownloadCleaner.CleanUpExpired"/>).
/// </summary>
internal sealed class DownloadCleanupService(IServiceProvider services, ILogger<DownloadCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                var deleted = services.GetRequiredService<DownloadCleaner>().CleanUpExpired(DateTimeOffset.UtcNow);
                if (deleted > 0)
                    logger.LogInformation("Cleaned up {Count} expired download folder(s)", deleted);
            }
            catch (Exception ex) { logger.LogError(ex, "Download clean-up sweep tick failed"); }
        }
        while (await timer.SafeWaitForNextTickAsync(stoppingToken));
    }
}
