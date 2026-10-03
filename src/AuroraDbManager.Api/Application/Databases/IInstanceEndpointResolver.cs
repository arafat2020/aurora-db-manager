using AuroraDbManager.Api.Domain.Instances;

namespace AuroraDbManager.Api.Application.Databases;

/// <summary>
/// Tells where the database server of an instance can be reached from this process. This is the
/// only thing a database manager needs to know about how an instance is hosted. Endpoints are for
/// internal use and are never returned by the API.
/// </summary>
public interface IInstanceEndpointResolver
{
    /// <exception cref="DatabaseOperationException">
    /// The instance's database server cannot be located or is not running.
    /// </exception>
    Task<InstanceEndpoint> ResolveAsync(Instance instance, CancellationToken cancellationToken);
}

public sealed record InstanceEndpoint(string Host, int Port);
