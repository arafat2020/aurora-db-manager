using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuroraDbManager.Api.Application.Monitoring;

/// <summary>
/// Looks up what a metric is tagged with when the code reporting it does not have it at hand.
/// A lookup that fails yields null, which is reported as <see cref="AuroraMetrics.Unknown"/>: a
/// metric is never worth failing the operation it describes.
/// </summary>
public static class MetricDimensions
{
    public static async Task<InstanceEngine?> EngineOfAsync(this AppDbContext db, Guid instanceId, CancellationToken cancellationToken)
    {
        try
        {
            var engines = await db.Instances.AsNoTracking()
                .Where(instance => instance.Id == instanceId)
                .Select(instance => instance.Engine)
                .ToListAsync(cancellationToken);
            return engines.Count == 0 ? null : engines[0];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    public static async Task<BackupStorageType?> StorageTypeOfAsync(this AppDbContext db, Guid? backupId, CancellationToken cancellationToken)
    {
        try
        {
            var storageTypes = await db.Backups.AsNoTracking()
                .Where(backup => backup.Id == backupId)
                .Select(backup => backup.StorageType)
                .ToListAsync(cancellationToken);
            return storageTypes.Count == 0 ? null : storageTypes[0];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }
}
