using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;

namespace AuroraDbManager.Web.Components;

/// <summary>The application's values as a person reads them.</summary>
public static class Labels
{
    public static string For(JobType type) => type switch
    {
        JobType.ProvisionInstance => "Provision instance",
        JobType.CreateDatabase => "Create database",
        JobType.DeleteDatabase => "Delete database",
        JobType.BackupDatabase => "Back up database",
        JobType.RestoreDatabase => "Restore database",
        _ => type.ToString()
    };

    public static string For(InstanceEngine engine) => engine switch
    {
        InstanceEngine.Postgres => "PostgreSQL",
        InstanceEngine.Mysql => "MySQL",
        _ => engine.ToString()
    };
}
