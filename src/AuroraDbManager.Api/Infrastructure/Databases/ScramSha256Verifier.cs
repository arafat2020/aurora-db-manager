using System.Security.Cryptography;
using System.Text;

namespace AuroraDbManager.Api.Infrastructure.Databases;

/// <summary>
/// What PostgreSQL stores instead of a password under SCRAM-SHA-256 (RFC 5802, RFC 7677):
/// <c>SCRAM-SHA-256$&lt;iterations&gt;:&lt;salt&gt;$&lt;StoredKey&gt;:&lt;ServerKey&gt;</c>. A role given this
/// as its password is given the password it was computed from, without the server, or anything
/// between here and it, ever seeing that password. It is what <c>psql</c>'s <c>\password</c> sends.
/// </summary>
/// <remarks>
/// The password is used as it is. RFC 4013 normalization leaves ASCII letters and digits
/// unchanged, and Aurora's generated passwords are nothing else.
/// </remarks>
public static class ScramSha256Verifier
{
    /// <summary>PostgreSQL's own defaults.</summary>
    public const int Iterations = 4096;
    private const int SaltLength = 16;

    public static string Create(string password) => Create(password, RandomNumberGenerator.GetBytes(SaltLength));

    public static string Create(string password, byte[] salt)
    {
        var salted = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        var storedKey = SHA256.HashData(HMACSHA256.HashData(salted, "Client Key"u8));
        var serverKey = HMACSHA256.HashData(salted, "Server Key"u8);
        CryptographicOperations.ZeroMemory(salted);

        return $"SCRAM-SHA-256${Iterations}:{Convert.ToBase64String(salt)}${Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}";
    }
}
