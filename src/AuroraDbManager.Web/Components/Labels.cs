using System.Globalization;
using AuroraDbManager.Api.Application.Instances;
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

    /// <summary>Every engine there is, as a phrase: "PostgreSQL or MySQL".</summary>
    public static string Engines => string.Join(" or ", Enum.GetValues<InstanceEngine>().Select(For));

    public static string Cpu(int cores) => cores == 1 ? "1 core" : $"{cores} cores";

    /// <summary>Megabytes as a person reads them: <c>512 MB</c>, <c>1.5 GB</c>, <c>8 GB</c>.</summary>
    public static string Memory(int megabytes) => megabytes < 1024
        ? $"{megabytes} MB"
        : $"{(megabytes / 1024d).ToString("0.##", CultureInfo.InvariantCulture)} GB";

    public static string Storage(int gigabytes) => $"{gigabytes} GB";

    /// <summary>What was found of an instance's database server, for someone who runs it rather than Docker.</summary>
    public static string For(InstanceHealthResponse health) => health.Reason switch
    {
        null => "The database server is running and accepting connections.",
        "database_not_ready" => "The database server is running but not accepting connections. It may be starting, recovering or overloaded.",
        "container_not_running" => "The database server is stopped.",
        "container_missing" => "The database server could not be found.",
        "runtime_unavailable" => "Docker could not be asked, so nothing is known about the database server right now. It may well be serving.",
        _ => "The database server is not working as it should."
    };

    /// <summary>Yes, no, or not known: what a check found, in the given words.</summary>
    public static string For(bool? found, string yes, string no) => found switch
    {
        true => yes,
        false => no,
        null => "Unknown"
    };
}
