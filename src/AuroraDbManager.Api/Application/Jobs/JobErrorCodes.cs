namespace AuroraDbManager.Api.Application.Jobs;

/// <summary>Error codes recorded on failed jobs.</summary>
public static class JobErrorCodes
{
    public const string ProvisioningFailed = "PROVISIONING_FAILED";
    public const string JobExecutionFailed = "JOB_EXECUTION_FAILED";
}
