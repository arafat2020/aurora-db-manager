using System.Globalization;
using System.Text;
using AuroraDbManager.Api.Application.Databases;

namespace AuroraDbManager.Api.Infrastructure.Backups;

/// <summary>
/// What a PostgreSQL backup artifact is and how the PostgreSQL programs are given a password.
/// Shared by the backup and the restore of that engine, so both agree on it.
/// </summary>
public static class PostgresDumpFormat
{
    public const string AdminUser = "postgres";
    public const string Extension = "dump";

    // Every custom-format archive starts with these bytes.
    private static readonly byte[] ArchiveMagic = "PGDMP"u8.ToArray();

    /// <summary>
    /// The content of a password file for <c>PGPASSFILE</c>, one line:
    /// <c>host:port:database:user:password</c>, with ':' and '\' escaped inside a field.
    /// </summary>
    public static string PasswordFile(InstanceEndpoint endpoint, string database, string adminPassword) =>
        string.Join(':', new[] { endpoint.Host, endpoint.Port.ToString(CultureInfo.InvariantCulture), database, AdminUser, adminPassword }
            .Select(field => field.Replace("\\", "\\\\").Replace(":", "\\:"))) + "\n";

    /// <summary>Whether the file starts like a custom-format archive.</summary>
    public static async Task<bool> IsArchiveAsync(string path, CancellationToken cancellationToken)
    {
        var header = new byte[ArchiveMagic.Length];
        await using var file = File.OpenRead(path);
        return await file.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken) == header.Length
            && header.AsSpan().SequenceEqual(ArchiveMagic);
    }
}

/// <summary>
/// What a MySQL backup artifact is and how the MySQL programs are given a password. Shared by the
/// backup and the restore of that engine, so both agree on it.
/// </summary>
public static class MySqlDumpFormat
{
    public const string AdminUser = "root";
    public const string Extension = "sql";

    // mysqldump ends every dump it finished with this comment; a dump cut short lacks it.
    private const string CompletionMarker = "-- Dump completed";
    private const int CompletionMarkerWindow = 512;

    /// <summary>
    /// The content of an option file for <c>--defaults-extra-file</c>; inside double quotes '\'
    /// and '"' are escaped with a backslash.
    /// </summary>
    public static string OptionFile(string adminPassword) =>
        $"[client]\npassword=\"{adminPassword.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"\n";

    /// <summary>Whether the file ends like a dump that <c>mysqldump</c> wrote to its end.</summary>
    public static async Task<bool> IsCompleteDumpAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        file.Seek(-Math.Min(file.Length, CompletionMarkerWindow), SeekOrigin.End);

        var tail = new byte[CompletionMarkerWindow];
        var read = await file.ReadAtLeastAsync(tail, tail.Length, throwOnEndOfStream: false, cancellationToken);
        return Encoding.UTF8.GetString(tail, 0, read).Contains(CompletionMarker, StringComparison.Ordinal);
    }
}
