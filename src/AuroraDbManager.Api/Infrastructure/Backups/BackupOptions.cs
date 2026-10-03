using AuroraDbManager.Api.Domain.Backups;

namespace AuroraDbManager.Api.Infrastructure.Backups;

/// <summary>Settings for backups, bound from the <c>Backups</c> configuration section.</summary>
public sealed class BackupOptions
{
    public const string SectionName = "Backups";

    /// <summary>
    /// Where new backups are stored: <c>local</c> or <c>s3</c>. Only ever what is written here;
    /// nothing about the environment selects a storage. It does not affect backups that exist:
    /// each of those stays in, and is restored from, the storage it was made for.
    /// </summary>
    public BackupStorageType StorageType { get; set; } = BackupStorageType.Local;

    public LocalBackupOptions Local { get; set; } = new();

    public S3BackupOptions S3 { get; set; } = new();

    public BackupToolOptions Tools { get; set; } = new();

    public RestoreOptions Restore { get; set; } = new();

    public BackupSchedulerOptions Scheduler { get; set; } = new();

    /// <summary>How long one backup attempt may run before the backup program is stopped.</summary>
    public int TimeoutSeconds { get; set; } = 3600;

    /// <summary>
    /// How long the backup program may take to connect to the instance's database server.
    /// Honoured by <c>pg_dump</c>; <c>mysqldump</c> has no such setting and is bound by <see cref="TimeoutSeconds"/> only.
    /// </summary>
    public int ConnectTimeoutSeconds { get; set; } = 10;

    /// <summary>Returns what is wrong with the settings of one storage, or null if that storage is usable.</summary>
    public string? StorageProblem(BackupStorageType storageType) => storageType switch
    {
        BackupStorageType.Local => string.IsNullOrWhiteSpace(Local.RootPath)
            ? "Backups:Local:RootPath is required for local backup storage."
            : null,
        BackupStorageType.S3 => S3.Validate(),
        _ => "Unknown backup storage type."
    };

    /// <summary>Returns what is wrong with the settings, or null if they are usable.</summary>
    public string? Validate()
    {
        if (!Enum.IsDefined(StorageType))
        {
            return "Backups:StorageType must be 'local' or 's3'.";
        }

        // Only the default storage has to be configured for the application to start. The other
        // one is checked when a backup that is in it is used.
        if (StorageProblem(StorageType) is { } storageError)
        {
            return storageError;
        }

        if (string.IsNullOrWhiteSpace(Tools.PgDumpPath) || string.IsNullOrWhiteSpace(Tools.MySqlDumpPath))
        {
            return "Backups:Tools:PgDumpPath and Backups:Tools:MySqlDumpPath are required.";
        }

        if (string.IsNullOrWhiteSpace(Tools.PgRestorePath) || string.IsNullOrWhiteSpace(Tools.MySqlPath))
        {
            return "Backups:Tools:PgRestorePath and Backups:Tools:MySqlPath are required.";
        }

        if (Restore.TimeoutSeconds is < 1 or > 604_800)
        {
            return "Backups:Restore:TimeoutSeconds must be between 1 and 604800.";
        }

        if (Scheduler.PollIntervalSeconds is < 1 or > 3600)
        {
            return "Backups:Scheduler:PollIntervalSeconds must be between 1 and 3600.";
        }

        if (Restore.LockTimeoutSeconds is < 1 or > 3600)
        {
            return "Backups:Restore:LockTimeoutSeconds must be between 1 and 3600.";
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
    /// path is taken relative to the directory the API was started in. Required for local storage only.
    /// </summary>
    public string RootPath { get; set; } = string.Empty;
}

/// <summary>Settings of the S3-compatible object storage, bound from <c>Backups:S3</c>.</summary>
public sealed class S3BackupOptions
{
    /// <summary>The bucket backups are stored in. It must exist; it is never created or chosen by a client.</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>The bucket's region, e.g. <c>us-east-1</c>. With a custom endpoint it is the region requests are signed for.</summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>
    /// The endpoint of an S3-compatible service, e.g. <c>https://s3.example.com</c>. Leave empty
    /// for AWS, where the endpoint follows from the region.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Static credentials. Leave both empty to use the AWS SDK's standard credential resolution:
    /// environment variables, the shared credentials file, or the role of the machine or container.
    /// They are secrets: supply them through the environment or a secret store, not a settings file.
    /// </summary>
    public string? AccessKey { get; set; }

    public string? SecretKey { get; set; }

    /// <summary>
    /// Whether the bucket is addressed in the path (<c>endpoint/bucket/key</c>) rather than in
    /// the host name. Needed by most S3-compatible services; AWS uses the host name.
    /// </summary>
    public bool PathStyle { get; set; }

    /// <summary>Optional prefix put before every object key, e.g. <c>aurora</c> or <c>env/prod</c>.</summary>
    public string? Prefix { get; set; }

    /// <summary>
    /// Directory in which a backup is written and validated before it is uploaded. Needs room for
    /// one backup per backup running at the same time. Empty for a directory under the system's
    /// temporary directory.
    /// </summary>
    public string? StagingPath { get; set; }

    /// <summary>How long uploading one backup, or downloading one for a restore, may take.</summary>
    public int UploadTimeoutSeconds { get; set; } = 3600;

    /// <summary>The prefix as it is used in keys: no leading or trailing slash, empty if there is none.</summary>
    public string NormalizedPrefix => (Prefix ?? string.Empty).Trim().Trim('/');

    /// <summary>Returns what is wrong with the settings, or null if they are usable.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Bucket))
        {
            return "Backups:S3:Bucket is required for S3 backup storage.";
        }

        if (string.IsNullOrWhiteSpace(Region))
        {
            return "Backups:S3:Region is required for S3 backup storage.";
        }

        if (!string.IsNullOrWhiteSpace(Endpoint)
            && !(Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) && endpoint.Scheme is "http" or "https"))
        {
            return "Backups:S3:Endpoint must be an http or https URL, or empty for AWS.";
        }

        // Either both, or neither and the standard credential resolution.
        if (string.IsNullOrWhiteSpace(AccessKey) != string.IsNullOrWhiteSpace(SecretKey))
        {
            return "Backups:S3:AccessKey and Backups:S3:SecretKey must be set together, or both left empty.";
        }

        if (NormalizedPrefix.Split('/').Any(segment => segment is "." or "..")
            || NormalizedPrefix.Contains("//", StringComparison.Ordinal)
            || NormalizedPrefix.Any(character => character == '\\' || char.IsControl(character)))
        {
            return "Backups:S3:Prefix must be a plain key prefix such as 'aurora' or 'env/prod'.";
        }

        if (UploadTimeoutSeconds is < 1 or > 86_400)
        {
            return "Backups:S3:UploadTimeoutSeconds must be between 1 and 86400.";
        }

        return null;
    }
}

/// <summary>Settings of the backup scheduler, bound from <c>Backups:Scheduler</c>.</summary>
public sealed class BackupSchedulerOptions
{
    /// <summary>
    /// How often the scheduler looks for schedules that are due. A scheduled backup starts at
    /// most this long after its time.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 30;
}

/// <summary>Settings for restoring backups, bound from <c>Backups:Restore</c>.</summary>
public sealed class RestoreOptions
{
    /// <summary>
    /// Directory into which a backup is copied or downloaded before it is restored. Needs room
    /// for one backup per restore running at the same time. It is the restores' own; backups in
    /// the making are staged elsewhere. Empty for a directory under the system's temporary directory.
    /// </summary>
    public string? StagingPath { get; set; }

    /// <summary>
    /// How long the restore program may run in one attempt. Restoring takes longer than dumping:
    /// indexes are rebuilt and constraints rechecked. The default is six hours.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 21_600;

    /// <summary>
    /// How long emptying the target database waits for locks held by sessions that reconnected
    /// after being ended, before the attempt fails and is retried.
    /// </summary>
    public int LockTimeoutSeconds { get; set; } = 60;
}

public sealed class BackupToolOptions
{
    /// <summary>The <c>pg_dump</c> program: a name looked up on <c>PATH</c>, or a full path.</summary>
    public string PgDumpPath { get; set; } = "pg_dump";

    /// <summary>The <c>mysqldump</c> program: a name looked up on <c>PATH</c>, or a full path.</summary>
    public string MySqlDumpPath { get; set; } = "mysqldump";

    /// <summary>The <c>pg_restore</c> program: a name looked up on <c>PATH</c>, or a full path.</summary>
    public string PgRestorePath { get; set; } = "pg_restore";

    /// <summary>The <c>mysql</c> client program: a name looked up on <c>PATH</c>, or a full path.</summary>
    public string MySqlPath { get; set; } = "mysql";
}
