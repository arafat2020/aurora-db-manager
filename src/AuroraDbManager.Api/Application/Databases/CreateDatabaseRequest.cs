namespace AuroraDbManager.Api.Application.Databases;

public sealed class CreateDatabaseRequest
{
    /// <summary>
    /// Name of the database, unique within its instance. Required, at most 63 characters, starting
    /// with a lowercase letter and containing only lowercase letters, digits and underscores.
    /// </summary>
    // Validated by DatabaseName rather than by attributes, so every name problem is reported
    // as DATABASE_NAME_INVALID.
    public string? Name { get; init; }
}
