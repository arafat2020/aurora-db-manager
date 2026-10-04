namespace AuroraDbManager.Api.Domain.Users;

/// <summary>What a user may do. Each role includes everything the one below it may do.</summary>
public enum UserRole
{
    /// <summary>Everything, including instances and users.</summary>
    Admin,

    /// <summary>Day-to-day operation: databases, backups, restores and schedules. No instances, no users.</summary>
    Operator,

    /// <summary>Reads only.</summary>
    Viewer
}
