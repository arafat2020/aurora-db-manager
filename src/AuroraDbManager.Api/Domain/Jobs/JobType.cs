namespace AuroraDbManager.Api.Domain.Jobs;

/// <summary>Kind of background work a job performs.</summary>
public enum JobType
{
    ProvisionInstance,
    CreateDatabase,
    DeleteDatabase,
    BackupDatabase,
    RestoreDatabase,

    /// <summary>Replaces the password of an instance's database administrator.</summary>
    RotateCredential
}
