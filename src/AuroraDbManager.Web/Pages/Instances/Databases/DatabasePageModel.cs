using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Web.Components;

namespace AuroraDbManager.Web.Pages.Instances.Databases;

/// <summary>A page about one database of one instance: both are in its address, and both have to be there.</summary>
public abstract class DatabasePageModel(InstanceService instances, DatabaseService databases) : ResourcePageModel
{
    public InstanceResponse Instance { get; private set; } = null!;

    public DatabaseResponse Database { get; private set; } = null!;

    /// <returns>What was not found, "Instance" or "Database"; null if both were.</returns>
    protected async Task<string?> LoadAsync(Guid id, Guid databaseId, CancellationToken cancellationToken)
    {
        if (await instances.GetAsync(id, cancellationToken) is not { } instance)
        {
            return "Instance";
        }

        // A database of another instance is not this instance's database, whatever its id.
        if (await databases.GetAsync(databaseId, cancellationToken) is not { } database || database.InstanceId != id)
        {
            return "Database";
        }

        Instance = instance;
        Database = database;
        return null;
    }
}
