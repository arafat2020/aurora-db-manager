namespace AuroraDbManager.Api.Errors;

public static class ErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string InstanceNotFound = "INSTANCE_NOT_FOUND";
    public const string InstanceProvisioning = "INSTANCE_PROVISIONING";
    public const string InstanceNotReady = "INSTANCE_NOT_READY";
    public const string DatabaseNotFound = "DATABASE_NOT_FOUND";
    public const string DatabaseNameInvalid = "DATABASE_NAME_INVALID";
    public const string DatabaseAlreadyExists = "DATABASE_ALREADY_EXISTS";
    public const string DatabaseCreating = "DATABASE_CREATING";
    public const string DatabaseDeleting = "DATABASE_DELETING";
    public const string DatabaseFailed = "DATABASE_FAILED";
    public const string DatabaseOperationInProgress = "DATABASE_OPERATION_IN_PROGRESS";
    public const string DatabaseNotReady = "DATABASE_NOT_READY";
    public const string BackupNotFound = "BACKUP_NOT_FOUND";
    public const string BackupOperationInProgress = "BACKUP_OPERATION_IN_PROGRESS";
    public const string JobNotFound = "JOB_NOT_FOUND";
    public const string NotFound = "NOT_FOUND";
    public const string MethodNotAllowed = "METHOD_NOT_ALLOWED";
    public const string UnsupportedMediaType = "UNSUPPORTED_MEDIA_TYPE";
    public const string RequestFailed = "REQUEST_FAILED";
    public const string InternalError = "INTERNAL_ERROR";
}
