using System.ComponentModel.DataAnnotations;

namespace AuroraDbManager.Api.Application.Credentials;

/// <summary>Settings for managed credentials, bound from the <c>Credentials</c> configuration section.</summary>
public sealed class CredentialOptions
{
    public const string SectionName = "Credentials";

    /// <summary>
    /// For how long after a rotation has completed its new password can be retrieved, once. After
    /// that it cannot be retrieved at all; the password itself goes on working.
    /// </summary>
    [Range(1, 1440)]
    public int ResultTtlMinutes { get; set; } = 15;
}
