using Microsoft.EntityFrameworkCore;

namespace SeatFlow.Services;

public sealed class ExpiredHoldCleanupWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ExpiredHoldCleanupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CleanupInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var cleanup = scope.ServiceProvider.GetRequiredService<ExpiredHoldCleanup>();
                var dbContext = scope.ServiceProvider.GetRequiredService<SeatFlow.Data.SeatFlowDbContext>();
                await using var transaction = await dbContext.Database.BeginTransactionAsync(stoppingToken);
                await cleanup.CleanupAsync(stoppingToken);
                await transaction.CommitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to release expired seat holds.");
            }
        }
    }
}
