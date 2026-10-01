using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Domain.Instances;

namespace AuroraDbManager.Api.Infrastructure.Docker;

/// <summary>Everything engine-specific about running a database image.</summary>
/// <param name="Image">Docker image reference, <c>repository:tag</c>.</param>
/// <param name="DataPath">Path inside the container where the instance's volume is mounted.</param>
/// <param name="AdminPasswordVariable">Environment variable the image reads the administrator password from.</param>
/// <param name="ReadinessCommand">
/// Command run inside the container that exits with 0 once the database accepts connections. It
/// connects over TCP on purpose: while an image initializes a new data directory it runs a
/// temporary server that listens on a Unix socket only, and that must not count as ready.
/// </param>
public sealed record DatabaseImage(
    string Image,
    string DataPath,
    string AdminPasswordVariable,
    IReadOnlyList<string> ReadinessCommand);

/// <summary>
/// The catalog of database images that may be run. An instance's engine and version are looked up
/// here; image names are never built from user input.
/// </summary>
public sealed class DockerImageResolver
{
    private static readonly string[] PostgresReadiness = ["pg_isready", "-q", "-h", "127.0.0.1", "-p", "5432"];
    private static readonly string[] MysqlReadiness = ["mysqladmin", "ping", "--silent", "-h", "127.0.0.1", "-P", "3306"];

    private static readonly Dictionary<(InstanceEngine Engine, string Version), DatabaseImage> Catalog = new()
    {
        [(InstanceEngine.Postgres, "15")] = Postgres("postgres:15"),
        [(InstanceEngine.Postgres, "16")] = Postgres("postgres:16"),
        [(InstanceEngine.Postgres, "17")] = Postgres("postgres:17"),
        [(InstanceEngine.Mysql, "8.0")] = Mysql("mysql:8.0"),
        [(InstanceEngine.Mysql, "8.4")] = Mysql("mysql:8.4")
    };

    public static IReadOnlyList<string> SupportedVersions(InstanceEngine engine) =>
        Catalog.Keys.Where(key => key.Engine == engine).Select(key => key.Version).Order().ToList();

    /// <exception cref="InstanceProvisioningException">The engine/version combination is not in the catalog.</exception>
    public DatabaseImage Resolve(InstanceEngine engine, string version)
    {
        if (Catalog.TryGetValue((engine, version), out var image))
        {
            return image;
        }

        var engineName = engine.ToString().ToLowerInvariant();
        throw new InstanceProvisioningException(
            DockerProvisioningErrors.UnsupportedDatabaseVersion,
            $"Version '{version}' of {engineName} is not supported. Supported versions: {string.Join(", ", SupportedVersions(engine))}.");
    }

    private static DatabaseImage Postgres(string image) =>
        new(image, "/var/lib/postgresql/data", "POSTGRES_PASSWORD", PostgresReadiness);

    private static DatabaseImage Mysql(string image) =>
        new(image, "/var/lib/mysql", "MYSQL_ROOT_PASSWORD", MysqlReadiness);
}
