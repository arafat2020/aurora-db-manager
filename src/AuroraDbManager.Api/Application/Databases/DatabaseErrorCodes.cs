namespace AuroraDbManager.Api.Application.Databases;

/// <summary>Error codes of failed database operations. They appear on failed jobs and failed databases.</summary>
public static class DatabaseErrorCodes
{
    /// <summary>The instance's database server could not be located or is not running.</summary>
    public const string DatabaseEngineUnavailable = "DATABASE_ENGINE_UNAVAILABLE";

    /// <summary>The database server was located but did not accept the connection.</summary>
    public const string DatabaseConnectionFailed = "DATABASE_CONNECTION_FAILED";
    public const string DatabaseOperationTimeout = "DATABASE_OPERATION_TIMEOUT";
    public const string DatabaseCreateFailed = "DATABASE_CREATE_FAILED";
    public const string DatabaseDeleteFailed = "DATABASE_DELETE_FAILED";

    /// <summary>The database's metadata is gone.</summary>
    public const string DatabaseDoesNotExist = "DATABASE_DOES_NOT_EXIST";

    /// <summary>The database is not in the status the operation starts from, or is not the job's.</summary>
    public const string DatabaseInvalidState = "DATABASE_INVALID_STATE";
    public const string InstanceNotFound = "INSTANCE_NOT_FOUND";
    public const string InstanceNotReady = "INSTANCE_NOT_READY";
}
