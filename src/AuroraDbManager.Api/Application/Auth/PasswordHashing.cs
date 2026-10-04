using AuroraDbManager.Api.Domain.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Application.Auth;

/// <summary>
/// Hashes and verifies passwords with ASP.NET Core's own password hasher: PBKDF2 with
/// HMAC-SHA512, a random salt per password, and a comparison that takes the same time whether
/// or not the password matches. No cryptography is implemented here.
/// </summary>
public sealed class PasswordHashing
{
    /// <summary>What OWASP recommends for PBKDF2-HMAC-SHA512. Stored in each hash, so it can be raised later.</summary>
    public const int Iterations = 210_000;

    private readonly PasswordHasher<User> _hasher =
        new(Options.Create(new PasswordHasherOptions { IterationCount = Iterations }));

    // Verified against when there is no user to verify against, so that signing in as someone
    // who does not exist takes as long as signing in with a wrong password.
    private readonly Lazy<string> _decoy;

    public PasswordHashing()
    {
        _decoy = new Lazy<string>(() => Hash(Guid.NewGuid().ToString("N")));
    }

    public string Hash(string password) => _hasher.HashPassword(null!, password);

    /// <param name="Succeeded">The password is the one the hash was made of.</param>
    /// <param name="NeedsRehash">The password matches a hash made with older parameters; hash it again.</param>
    public readonly record struct Verification(bool Succeeded, bool NeedsRehash);

    public Verification Verify(string passwordHash, string password)
    {
        try
        {
            var result = _hasher.VerifyHashedPassword(null!, passwordHash, password);
            return new Verification(result != PasswordVerificationResult.Failed, result == PasswordVerificationResult.SuccessRehashNeeded);
        }
        catch (FormatException)
        {
            // A stored value that is no hash at all matches no password.
            return new Verification(false, false);
        }
    }

    /// <summary>Does the work of a verification, for nobody.</summary>
    public void VerifyAgainstNoUser(string password) => Verify(_decoy.Value, password);
}
