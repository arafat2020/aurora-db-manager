namespace AuroraDbManager.Api.Infrastructure.Secrets;

/// <summary>Persistence model for an instance's encrypted credentials.</summary>
public sealed class InstanceSecret
{
    public Guid InstanceId { get; init; }

    /// <summary>Administrator password, encrypted with ASP.NET Core Data Protection.</summary>
    public string ProtectedAdminPassword { get; init; } = null!;

    public DateTime CreatedAt { get; init; }
}
