using System.Globalization;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Domain.Backups;
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

    public static string For(BackupStorageType storage) => storage switch
    {
        BackupStorageType.Local => "Local",
        BackupStorageType.S3 => "S3",
        _ => storage.ToString()
    };

    /// <summary>Where a storage keeps backups, in a sentence. Never which directory, bucket or key.</summary>
    public static string Describe(BackupStorageType storage) => storage switch
    {
        BackupStorageType.Local => "A directory on the server Aurora runs on.",
        BackupStorageType.S3 => "A bucket of an S3-compatible object store.",
        _ => string.Empty
    };

    public static string For(BackupChecksumAlgorithm algorithm) => algorithm switch
    {
        BackupChecksumAlgorithm.Sha256 => "SHA-256",
        _ => algorithm.ToString()
    };

    /// <summary>A number of bytes as a person reads it: <c>512 B</c>, <c>1.5 KB</c>, <c>184 MB</c>, <c>2.25 GB</c>.</summary>
    public static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value.ToString(unit == 0 ? "0" : "0.##", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    /// <summary>A value as the API writes it and takes it in a query: <c>provision_instance</c>, <c>pending</c>.</summary>
    public static string ApiValue<TEnum>(TEnum value)
        where TEnum : struct, Enum => System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    /// <summary>
    /// An instant as the clock of a time zone shows it, for reading next to the instant itself:
    /// <c>2026-10-06 00:00</c>. Null if this machine does not know the zone; the instant is still
    /// what it is. Only how a time is written: when a schedule runs is never worked out here.
    /// </summary>
    public static string? InZone(DateTime utc, string timeZoneId)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone)
                .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }
}
