using System.Text;
using AuroraDbManager.Api.Domain.Users;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Application.Auth;

/// <summary>Settings for signing in, bound from the <c>Authentication</c> configuration section.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Authentication";

    public JwtOptions Jwt { get; set; } = new();

    public BootstrapAdminOptions BootstrapAdmin { get; set; } = new();

    /// <summary>Returns what is wrong with the settings, or null if they are usable. Never quotes a secret.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Jwt.Issuer) || string.IsNullOrWhiteSpace(Jwt.Audience))
        {
            return "Authentication:Jwt:Issuer and Authentication:Jwt:Audience are required.";
        }

        if (string.IsNullOrWhiteSpace(Jwt.SigningKey))
        {
            return "Authentication:Jwt:SigningKey is required. Supply a random secret of at least "
                + $"{JwtOptions.MinSigningKeyBytes} bytes through the environment or a secret store; there is no default.";
        }

        if (Encoding.UTF8.GetByteCount(Jwt.SigningKey) < JwtOptions.MinSigningKeyBytes)
        {
            return $"Authentication:Jwt:SigningKey must be at least {JwtOptions.MinSigningKeyBytes} bytes long.";
        }

        if (Jwt.AccessTokenLifetimeMinutes is < 1 or > 1440)
        {
            return "Authentication:Jwt:AccessTokenLifetimeMinutes must be between 1 and 1440.";
        }

        // Neither, which is the normal state once an administrator exists, or both and usable.
        if (BootstrapAdmin.IsConfigured)
        {
            if (!User.IsValidUsername(BootstrapAdmin.Username))
            {
                return $"Authentication:BootstrapAdmin:Username must be {User.UsernameMinLength} to {User.UsernameMaxLength} "
                    + "letters, digits, dots, dashes or underscores.";
            }

            if (BootstrapAdmin.Password is not { Length: >= User.PasswordMinLength and <= User.PasswordMaxLength })
            {
                return $"Authentication:BootstrapAdmin:Password must be {User.PasswordMinLength} to {User.PasswordMaxLength} characters long.";
            }
        }
        else if (!string.IsNullOrEmpty(BootstrapAdmin.Username) || !string.IsNullOrEmpty(BootstrapAdmin.Password))
        {
            return "Authentication:BootstrapAdmin:Username and Authentication:BootstrapAdmin:Password must be set together, or both left empty.";
        }

        return null;
    }
}

/// <summary>Settings of the access tokens, bound from <c>Authentication:Jwt</c>.</summary>
public sealed class JwtOptions
{
    /// <summary>HMAC-SHA256 takes a key of at least its output size.</summary>
    public const int MinSigningKeyBytes = 32;

    public string Issuer { get; set; } = "aurora-db-manager";

    public string Audience { get; set; } = "aurora-db-manager-api";

    /// <summary>
    /// The secret access tokens are signed with. Whoever knows it can sign in as anyone. It has
    /// no default and is never logged: supply it through the environment or a secret store, not
    /// a settings file. Changing it invalidates every token issued so far.
    /// </summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>
    /// How long an access token is valid. Tokens are not revoked: a user who is disabled, deleted
    /// or given another role keeps what the token says until it expires, so keep this short.
    /// </summary>
    public int AccessTokenLifetimeMinutes { get; set; } = 30;
}

/// <summary>
/// The first administrator, bound from <c>Authentication:BootstrapAdmin</c>. Used only while no
/// administrator exists; afterwards it is ignored and should be removed from the configuration.
/// </summary>
public sealed class BootstrapAdminOptions
{
    public string? Username { get; set; }

    /// <summary>A secret: supply it through the environment or a secret store. Never logged, never returned.</summary>
    public string? Password { get; set; }

    public bool IsConfigured => !string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Password);
}

/// <summary>Fails startup with the exact setting that is missing or wrong.</summary>
public sealed class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthOptions options) =>
        options.Validate() is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
}
