using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Infrastructure.Backups;

/// <summary>Fails startup with the exact setting that is missing or wrong.</summary>
public sealed class BackupOptionsValidator : IValidateOptions<BackupOptions>
{
    public ValidateOptionsResult Validate(string? name, BackupOptions options) =>
        options.Validate() is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
}
