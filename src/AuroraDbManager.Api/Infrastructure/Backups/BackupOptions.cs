namespace AuroraDbManager.Api.Infrastructure.Backups;

/// <summary>Settings for backups, bound from the <c>Backups</c> configuration section.</summary>
public sealed class BackupOptions
{
    public const string SectionName = "Backups";

    public LocalBackupOptions Local { get; set; } = new();

    public BackupToolOptions Tools { get; set; } = new();

    /// <summary>How long one backup attempt may run before the backup program is stopped.</summary>
    public int TimeoutSeconds { get; set; } = 3600;

    /// <summary>
    /// How long the backup program may take to connect to the instance's database server.
    /// Honoured by <c>pg_dump</c>; <c>mysqldump</c> has no such setting and is bound by <see cref="TimeoutSeconds"/> only.
    /// </summary>
    public int ConnectTimeoutSeconds { get; set; } = 10;

    /// <summary>Returns what is wrong with the settings, or null if they are usable.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Local.RootPath))
        {
            return "Backups:Local:RootPath is required.";
        }

        if (string.IsNullOrWhiteSpace(Tools.PgDumpPath) || string.IsNullOrWhiteSpace(Tools.MySqlDumpPath))
        {
            return "Backups:Tools:PgDumpPath and Backups:Tools:MySqlDumpPath are required.";
        }

        if (TimeoutSeconds is < 1 or > 86_400)
        {
            return "Backups:TimeoutSeconds must be between 1 and 86400.";
        }

        if (ConnectTimeoutSeconds is < 1 or > 300)
        {
            return "Backups:ConnectTimeoutSeconds must be between 1 and 300.";
        }

        return null;
    }
}

public sealed class LocalBackupOptions
{
    /// <summary>
    /// Directory under which all local backups are kept. Created when first needed. A relative
    /// path is taken relative to the directory the API was started in.
    /// </summary>
    public string RootPath { get; set; } = string.Empty;
}

public sealed class BackupToolOptions
{
    /// <summary>The <c>pg_dump</c> program: a name looked up on <c>PATH</c>, or a full path.</summary>
    public string PgDumpPath { get; set; } = "pg_dump";

    /// <summary>The <c>mysqldump</c> program: a name looked up on <c>PATH</c>, or a full path.</summary>
    public string MySqlDumpPath { get; set; } = "mysqldump";
}
