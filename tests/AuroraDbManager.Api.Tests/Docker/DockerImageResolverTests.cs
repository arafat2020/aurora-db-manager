using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Infrastructure.Docker;

namespace AuroraDbManager.Api.Tests.Docker;

public sealed class DockerImageResolverTests
{
    private readonly DockerImageResolver _resolver = new();

    [Theory]
    [InlineData(InstanceEngine.Postgres, "15", "postgres:15")]
    [InlineData(InstanceEngine.Postgres, "16", "postgres:16")]
    [InlineData(InstanceEngine.Postgres, "17", "postgres:17")]
    [InlineData(InstanceEngine.Mysql, "8.0", "mysql:8.0")]
    [InlineData(InstanceEngine.Mysql, "8.4", "mysql:8.4")]
    public void Resolve_SupportedVersion_ReturnsCatalogImage(InstanceEngine engine, string version, string expectedImage)
    {
        Assert.Equal(expectedImage, _resolver.Resolve(engine, version).Image);
    }

    [Fact]
    public void Resolve_Postgres_UsesPostgresDataDirectoryPasswordVariableAndReadinessCheck()
    {
        var image = _resolver.Resolve(InstanceEngine.Postgres, "16");

        Assert.Equal("/var/lib/postgresql/data", image.DataPath);
        Assert.Equal("POSTGRES_PASSWORD", image.AdminPasswordVariable);
        Assert.Equal("pg_isready", image.ReadinessCommand[0]);
    }

    [Fact]
    public void Resolve_Mysql_UsesMysqlDataDirectoryPasswordVariableAndReadinessCheck()
    {
        var image = _resolver.Resolve(InstanceEngine.Mysql, "8.4");

        Assert.Equal("/var/lib/mysql", image.DataPath);
        Assert.Equal("MYSQL_ROOT_PASSWORD", image.AdminPasswordVariable);
        Assert.Equal(["mysqladmin", "ping"], image.ReadinessCommand.Take(2));
    }

    [Theory]
    [InlineData(InstanceEngine.Postgres, "9.6")]
    [InlineData(InstanceEngine.Postgres, "latest")]
    [InlineData(InstanceEngine.Postgres, "16.2")]
    [InlineData(InstanceEngine.Postgres, "8.4")] // a MySQL version
    [InlineData(InstanceEngine.Mysql, "16")] // a PostgreSQL version
    [InlineData(InstanceEngine.Mysql, "5.7")]
    [InlineData(InstanceEngine.Postgres, "16 --privileged")]
    [InlineData(InstanceEngine.Postgres, "evil/image:1")]
    public void Resolve_UnsupportedVersion_FailsWithControlledError(InstanceEngine engine, string version)
    {
        var exception = Assert.Throws<InstanceProvisioningException>(() => _resolver.Resolve(engine, version));

        Assert.Equal("UNSUPPORTED_DATABASE_VERSION", exception.Code);
        Assert.Contains("Supported versions:", exception.Message);
    }

    [Fact]
    public void Resolve_UnknownEngine_FailsWithControlledError()
    {
        var exception = Assert.Throws<InstanceProvisioningException>(() => _resolver.Resolve((InstanceEngine)99, "1"));

        Assert.Equal("UNSUPPORTED_DATABASE_VERSION", exception.Code);
    }

    [Fact]
    public void SupportedVersions_ListsTheCatalogPerEngine()
    {
        Assert.Equal(["15", "16", "17"], DockerImageResolver.SupportedVersions(InstanceEngine.Postgres));
        Assert.Equal(["8.0", "8.4"], DockerImageResolver.SupportedVersions(InstanceEngine.Mysql));
    }
}
