using System.Security.Claims;
using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;

namespace AuroraDbManager.Web.Components;

/// <summary>
/// Which actions a page offers: the ones the user's role may take, on something whose status on
/// record does not already rule them out. It decides what is offered and nothing else. Whether
/// an action is carried out is for the policy of its page and for the service, which looks at
/// more than a page can know, a backup in progress for instance, and may still say no.
/// </summary>
public static class Offered
{
    public static bool CreateInstance(ClaimsPrincipal user) => user.IsAdmin();

    public static bool DeleteInstance(ClaimsPrincipal user, InstanceResponse instance) =>
        user.IsAdmin() && instance.Status != InstanceStatus.Provisioning;

    /// <summary>Turning external access on or off restarts the server, so there has to be one that is running.</summary>
    public static bool ChangeExternalAccess(ClaimsPrincipal user, InstanceResponse instance) =>
        user.IsAdmin() && instance.Status == InstanceStatus.Running;

    public static bool CreateDatabase(ClaimsPrincipal user, InstanceResponse instance) =>
        user.IsOperator() && instance.Status == InstanceStatus.Running;

    public static bool CreateBackup(ClaimsPrincipal user, InstanceResponse instance, DatabaseResponse database) =>
        user.IsOperator() && instance.Status == InstanceStatus.Running && database.Status == DatabaseStatus.Ready;

    /// <summary>Only a finished backup holds anything to restore, and only a ready database of a running instance can take it.</summary>
    public static bool RestoreBackup(ClaimsPrincipal user, InstanceResponse instance, DatabaseResponse database, BackupResponse backup) =>
        CreateBackup(user, instance, database) && backup.Status == BackupStatus.Completed;

    public static bool DeleteDatabase(ClaimsPrincipal user, InstanceResponse instance, DatabaseResponse database) =>
        user.IsOperator() && instance.Status == InstanceStatus.Running && database.Status == DatabaseStatus.Ready;
}
