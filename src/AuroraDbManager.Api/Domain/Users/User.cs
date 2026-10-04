namespace AuroraDbManager.Api.Domain.Users;

/// <summary>
/// Someone who may sign in to this installation. A user has a name, a role and a password, of
/// which only a hash is ever kept. There is one installation and no ownership: what a user may do
/// follows from the role alone.
/// </summary>
public sealed class User
{
    public const int UsernameMinLength = 3;
    public const int UsernameMaxLength = 64;
    public const int PasswordMinLength = 12;
    public const int PasswordMaxLength = 128;
    public const int PasswordHashMaxLength = 512;

    private User()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The name as it was given when the user was created.</summary>
    public string Username { get; private set; } = null!;

    /// <summary>The name as it is compared: <see cref="Normalize"/>. Unique, so names differing only in case are one name.</summary>
    public string NormalizedUsername { get; private set; } = null!;

    /// <summary>The password hasher's output: algorithm, parameters, salt and hash. Never the password, and never returned by the API.</summary>
    public string PasswordHash { get; private set; } = null!;

    public UserRole Role { get; private set; }

    /// <summary>A disabled user cannot sign in.</summary>
    public bool Enabled { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public static User Create(string username, string passwordHash, UserRole role, bool enabled, DateTime utcNow)
    {
        if (!IsValidUsername(username))
        {
            throw new ArgumentException("The username is not valid.", nameof(username));
        }

        var user = new User
        {
            Id = Guid.CreateVersion7(),
            Username = username,
            NormalizedUsername = Normalize(username),
            Role = role,
            Enabled = enabled,
            CreatedAt = utcNow
        };
        user.ChangePasswordHash(passwordHash, utcNow);
        return user;
    }

    public void ChangeRole(UserRole role, DateTime utcNow)
    {
        Role = role;
        UpdatedAt = utcNow;
    }

    public void SetEnabled(bool enabled, DateTime utcNow)
    {
        Enabled = enabled;
        UpdatedAt = utcNow;
    }

    public void ChangePasswordHash(string passwordHash, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(passwordHash.Length, PasswordHashMaxLength, nameof(passwordHash));

        PasswordHash = passwordHash;
        UpdatedAt = utcNow;
    }

    /// <summary>
    /// Whether the text can be a username: 3 to 64 letters, digits, dots, dashes and underscores.
    /// No whitespace anywhere, so a name can neither be blank nor differ from another by a space.
    /// </summary>
    public static bool IsValidUsername(string? username) =>
        username is { Length: >= UsernameMinLength and <= UsernameMaxLength }
        && username.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    /// <summary>The form two usernames are compared in: case does not distinguish them.</summary>
    public static string Normalize(string username) => username.ToUpperInvariant();
}
