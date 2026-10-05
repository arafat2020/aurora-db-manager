namespace AuroraDbManager.Web.Components;

/// <summary>The addresses of the pages about instances and their databases, in one place.</summary>
public static class Routes
{
    public const string Instances = "/instances";
    public const string CreateInstance = "/instances/create";
    public const string Jobs = "/jobs";

    public static string Instance(Guid id) => $"/instances/{id:D}";

    public static string DeleteInstance(Guid id) => $"{Instance(id)}/delete";

    public static string Databases(Guid instanceId) => $"{Instance(instanceId)}/databases";

    public static string CreateDatabase(Guid instanceId) => $"{Databases(instanceId)}/create";

    public static string Database(Guid instanceId, Guid databaseId) => $"{Databases(instanceId)}/{databaseId:D}";

    public static string DeleteDatabase(Guid instanceId, Guid databaseId) => $"{Database(instanceId, databaseId)}/delete";
}
