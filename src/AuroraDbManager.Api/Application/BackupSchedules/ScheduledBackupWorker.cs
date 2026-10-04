using AuroraDbManager.Api.Infrastructure.Backups;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Application.BackupSchedules;

/// <summary>
/// Wakes up every <c>Backups:Scheduler:PollIntervalSeconds</c> and lets <see cref="BackupScheduler"/>
/// deal with the schedules that are due, starting right after startup so that what came due while
/// the application was down is not left waiting. It keeps nothing in memory between passes: what
/// is due, and what has been dealt with, is in the database.
/// </summary>
public sealed class ScheduledBackupWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<BackupOptions> options,
    TimeProvider timeProvider,
    ILogger<ScheduledBackupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.Scheduler.PollIntervalSeconds);
        logger.LogInformation("Backup scheduler started; checking every {PollIntervalSeconds}s", interval.TotalSeconds);

        try
        {
            while (true)
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<BackupScheduler>().RunDueAsync(stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // For example the system database being unreachable; tried again at the next pass.
                    // The pass has counted and logged its own failure; this covers what fails around it.
                    logger.LogDebug(exception, "The backup scheduler could not check for due schedules");
                }

                await Task.Delay(interval, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
