namespace AuroraDbManager.Api.Infrastructure.Secrets;

/// <summary>Persistence model for an instance's encrypted credentials.</summary>
public sealed class InstanceSecret
{
    public Guid InstanceId { get; init; }

    /// <summary>Administrator password, encrypted with ASP.NET Core Data Protection.</summary>
    public string ProtectedAdminPassword { get; init; } = null!;

    /// <summary>
    /// The password that is to replace the administrator password, encrypted the same way. Set
    /// from the moment a rotation is accepted until the database server is known to accept it and
    /// it has become <see cref="ProtectedAdminPassword"/>; null otherwise.
    /// </summary>
    public string? ProtectedPendingAdminPassword { get; init; }

    /// <summary>
    /// The completed rotation whose new password, the one in <see cref="ProtectedAdminPassword"/>,
    /// may be handed out once. Null if no rotation has completed since the password was stored,
    /// or a newer rotation has been accepted.
    /// </summary>
    public Guid? DeliveryJobId { get; init; }

    /// <summary>Until when it may be handed out.</summary>
    public DateTime? DeliveryExpiresAt { get; init; }

    /// <summary>When it was handed out; null until then. It is handed out once.</summary>
    public DateTime? DeliveryConsumedAt { get; init; }

    public DateTime CreatedAt { get; init; }
}
