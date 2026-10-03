namespace AuroraDbManager.Api.Domain.Jobs;

/// <summary>Kind of background work a job performs.</summary>
public enum JobType
{
    ProvisionInstance,
    CreateDatabase,
    DeleteDatabase,
    BackupDatabase
}
