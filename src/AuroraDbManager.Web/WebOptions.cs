using Microsoft.Extensions.Options;

namespace AuroraDbManager.Web;

/// <summary>
/// Settings of the UI itself, bound from the <c>Web</c> configuration section. Everything else
/// the UI host needs, the system database, Docker, backups, authentication and security, is the
/// application's own configuration and is not repeated here.
/// </summary>
public sealed class WebOptions
{
    public const string SectionName = "Web";

    /// <summary>The name shown in the header and in page titles: what this installation is called.</summary>
    public string ApplicationName { get; set; } = "Aurora";

    /// <summary>
    /// How long a sign-in lasts before the user signs in again. Fixed from the moment of signing
    /// in; using the UI does not extend it.
    /// </summary>
    public int SessionHours { get; set; } = 8;

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(ApplicationName) || ApplicationName.Length > 40)
        {
            return $"{SectionName}:ApplicationName must be 1 to 40 characters.";
        }

        if (SessionHours is < 1 or > 168)
        {
            return $"{SectionName}:SessionHours must be between 1 and 168.";
        }

        return null;
    }
}

/// <summary>Fails startup with the exact setting that is missing or wrong.</summary>
public sealed class WebOptionsValidator : IValidateOptions<WebOptions>
{
    public ValidateOptionsResult Validate(string? name, WebOptions options) =>
        options.Validate() is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
}
