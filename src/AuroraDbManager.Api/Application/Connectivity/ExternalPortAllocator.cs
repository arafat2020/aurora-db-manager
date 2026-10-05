using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Application.Connectivity;

/// <summary>
/// Picks the host port for an instance: the lowest port of the configured range that no instance
/// has on record and that nothing is published on. Picking is not reserving. Two requests can be
/// given the same port; the unique index on <c>instances.external_port</c> lets only one of them
/// record it, and Docker refuses a port that something else took in the meantime. Whoever is
/// refused asks again, naming the port to leave out.
/// </summary>
public sealed class ExternalPortAllocator(AppDbContext db, IInstanceNetwork network, IOptions<ExternalAccessOptions> options)
{
    /// <returns>A port to try, or null if the range has none left.</returns>
    /// <exception cref="Instances.InstanceProvisioningException">The runtime could not be asked which ports are published.</exception>
    public async Task<int?> NextAsync(IReadOnlySet<int> excluded, CancellationToken cancellationToken)
    {
        var settings = options.Value;

        // Read every time: the records and the runtime are the state, also after a restart.
        var recorded = await db.Instances.AsNoTracking()
            .Where(instance => instance.ExternalPort != null)
            .Select(instance => instance.ExternalPort!.Value)
            .ToListAsync(cancellationToken);
        var published = await network.PublishedHostPortsAsync(cancellationToken);

        var taken = recorded.ToHashSet();
        for (var port = settings.PortRangeStart; port <= settings.PortRangeEnd; port++)
        {
            if (!taken.Contains(port) && !published.Contains(port) && !excluded.Contains(port))
            {
                return port;
            }
        }

        return null;
    }
}
