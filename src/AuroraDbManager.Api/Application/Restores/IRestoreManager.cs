using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;

namespace AuroraDbManager.Api.Application.Restores;

/// <summary>
/// Replaces the contents of a database with those of one of its backups. There is one
/// implementation per <see cref="InstanceEngine"/>; how an engine is restored, and with which
/// program, is its business alone.
/// </summary>
public interface IRestoreManager
{
    /// <summary>The engine this implementation restores databases of.</summary>
    InstanceEngine Engine { get; }

    /// <summary>
    /// Makes <paramref name="database"/> of <paramref name="instance"/> contain what
    /// <paramref name="backup"/> contains, and nothing else: what is in the database now is
    /// removed. Returns once the restored database has been found usable. One attempt, no
    /// retries. May be called again after a failed or interrupted attempt, whatever that attempt
    /// left in the database: every attempt starts by emptying it. The backup is only read.
    /// </summary>
    /// <param name="instance">The instance the database is in.</param>
    /// <param name="database">The database to restore into; the one the backup was made of.</param>
    /// <param name="backup">A completed backup of that database.</param>
    /// <param name="jobId">The job this attempt belongs to; it names the attempt's working directory.</param>
    /// <param name="cancellationToken">Stops the attempt. The database may then be empty or partly restored.</param>
    /// <exception cref="RestoreOperationException">The attempt failed.</exception>
    Task RestoreAsync(Instance instance, Database database, Backup backup, Guid jobId, CancellationToken cancellationToken);
}
