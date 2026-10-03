using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;

namespace AuroraDbManager.Api.Application.Databases;

/// <summary>
/// Creates and removes logical databases inside the database server of an instance. There is one
/// implementation per <see cref="InstanceEngine"/>; engine-specific SQL lives in the
/// implementations and nowhere else. An implementation talks to the database server the instance
/// represents, over the engine's own protocol, and knows nothing about how that server is hosted.
/// </summary>
/// <remarks>
/// Each call performs one attempt and does not retry; the job running it does. Failures are
/// reported as <see cref="DatabaseOperationException"/>.
/// </remarks>
public interface IDatabaseManager
{
    /// <summary>The engine this implementation manages databases for.</summary>
    InstanceEngine Engine { get; }

    /// <summary>
    /// Creates <paramref name="database"/> in the server of <paramref name="instance"/>. Succeeds
    /// if it already exists, so an interrupted attempt can be repeated.
    /// </summary>
    Task CreateDatabaseAsync(Instance instance, Database database, CancellationToken cancellationToken);

    /// <summary>
    /// Removes <paramref name="database"/> and its data from the server of
    /// <paramref name="instance"/>. Succeeds if it does not exist.
    /// </summary>
    Task DeleteDatabaseAsync(Instance instance, Database database, CancellationToken cancellationToken);
}
