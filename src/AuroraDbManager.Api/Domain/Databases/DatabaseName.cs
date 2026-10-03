namespace AuroraDbManager.Api.Domain.Databases;

/// <summary>
/// The rule for database names. It is deliberately narrower than what PostgreSQL or MySQL accept:
/// a name that passes is a valid identifier in both engines without quoting, and means the same
/// database whether or not the engine folds case. Names are never rewritten, only accepted or rejected.
/// </summary>
public static class DatabaseName
{
    /// <summary>PostgreSQL truncates identifiers to 63 bytes; MySQL allows 64 characters.</summary>
    public const int MaxLength = 63;

    // Databases the engines create themselves.
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "postgres", "template0", "template1",
        "mysql", "sys", "information_schema", "performance_schema"
    };

    /// <summary>Returns why <paramref name="name"/> is not acceptable, or null if it is.</summary>
    public static string? Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "name is required.";
        }

        if (name.Length > MaxLength)
        {
            return $"name must be at most {MaxLength} characters.";
        }

        if (!IsLowercaseLetter(name[0]) || !name.All(c => IsLowercaseLetter(c) || char.IsAsciiDigit(c) || c == '_'))
        {
            return "name must start with a lowercase letter and contain only lowercase letters, digits and underscores.";
        }

        if (Reserved.Contains(name))
        {
            return $"name '{name}' is reserved by the database engine.";
        }

        return null;
    }

    private static bool IsLowercaseLetter(char c) => c is >= 'a' and <= 'z';
}
