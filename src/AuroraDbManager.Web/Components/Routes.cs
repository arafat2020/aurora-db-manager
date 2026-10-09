namespace AuroraDbManager.Web.Components;

/// <summary>The addresses of the pages about instances and their databases, in one place.</summary>
public static class Routes
{
    public const string Instances = "/instances";
    public const string CreateInstance = "/instances/create";
    public const string Jobs = "/jobs";
    public const string Monitoring = "/monitoring";

    public static string Job(Guid id) => $"/jobs/{id:D}";

    /// <summary>The jobs of one instance, or of one database: the list, narrowed by the filter the job service has for it.</summary>
    public static string JobsOfInstance(Guid instanceId) => $"/jobs?instanceId={instanceId:D}";

    public static string JobsOfDatabase(Guid databaseId) => $"/jobs?databaseId={databaseId:D}";

    public static string Instance(Guid id) => $"/instances/{id:D}";

    public static string DeleteInstance(Guid id) => $"{Instance(id)}/delete";

    public static string ExternalAccess(Guid id) => $"{Instance(id)}/external-access";

    public static string RotatePassword(Guid id) => $"{Instance(id)}/rotate-password";

    public static string RotatePasswordResult(Guid id, Guid jobId) => $"{RotatePassword(id)}/{jobId:D}/result";

    public static string Databases(Guid instanceId) => $"{Instance(instanceId)}/databases";

    public static string CreateDatabase(Guid instanceId) => $"{Databases(instanceId)}/create";

    public static string Database(Guid instanceId, Guid databaseId) => $"{Databases(instanceId)}/{databaseId:D}";

    public static string DeleteDatabase(Guid instanceId, Guid databaseId) => $"{Database(instanceId, databaseId)}/delete";

    public static string Schedule(Guid instanceId, Guid databaseId) => $"{Database(instanceId, databaseId)}/schedule";

    public static string EditSchedule(Guid instanceId, Guid databaseId) => $"{Schedule(instanceId, databaseId)}/edit";

    public static string DeleteSchedule(Guid instanceId, Guid databaseId) => $"{Schedule(instanceId, databaseId)}/delete";

    public static string Backups(Guid instanceId, Guid databaseId) => $"{Database(instanceId, databaseId)}/backups";

    public static string CreateBackup(Guid instanceId, Guid databaseId) => $"{Backups(instanceId, databaseId)}/create";

    public static string Backup(Guid instanceId, Guid databaseId, Guid backupId) => $"{Backups(instanceId, databaseId)}/{backupId:D}";

    public static string RestoreBackup(Guid instanceId, Guid databaseId, Guid backupId) => $"{Backup(instanceId, databaseId, backupId)}/restore";
}
