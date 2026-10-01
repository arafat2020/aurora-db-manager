using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Infrastructure.Docker;

namespace AuroraDbManager.Api.Tests.Docker;

public sealed class DockerContainerSpecTests
{
    private static readonly DockerImageResolver Images = new();

    private static Instance NewInstance(InstanceEngine engine, string version, int cpu = 2, int memoryMb = 1024) =>
        Instance.Create("orders", engine, version, cpu, memoryMb, 20, DateTime.UtcNow);

    [Fact]
    public void For_PostgresInstance_DescribesItsContainer()
    {
        var instance = NewInstance(InstanceEngine.Postgres, "16");

        var spec = DockerContainerSpec.For(instance, Images.Resolve(instance.Engine, instance.Version), "s3cret", "aurora-db");

        Assert.Equal($"aurora-instance-{instance.Id}", spec.Name);
        Assert.Equal("postgres:16", spec.Image);
        Assert.Equal("aurora-db", spec.NetworkName);
        Assert.Equal($"aurora-instance-{instance.Id}-data", spec.VolumeName);
        Assert.Equal("/var/lib/postgresql/data", spec.VolumeTarget);
        Assert.Equal(new Dictionary<string, string> { ["POSTGRES_PASSWORD"] = "s3cret" }, spec.Environment);
        Assert.True(DockerResourceNaming.IsOwnedBy(spec.Labels, instance.Id));
    }

    [Fact]
    public void For_MysqlInstance_DescribesItsContainer()
    {
        var instance = NewInstance(InstanceEngine.Mysql, "8.4");

        var spec = DockerContainerSpec.For(instance, Images.Resolve(instance.Engine, instance.Version), "s3cret", "aurora-db");

        Assert.Equal("mysql:8.4", spec.Image);
        Assert.Equal("/var/lib/mysql", spec.VolumeTarget);
        Assert.Equal(new Dictionary<string, string> { ["MYSQL_ROOT_PASSWORD"] = "s3cret" }, spec.Environment);
    }

    [Theory]
    [InlineData(1, 512, 1_000_000_000L, 536_870_912L)]
    [InlineData(2, 1024, 2_000_000_000L, 1_073_741_824L)]
    [InlineData(16, 65_536, 16_000_000_000L, 68_719_476_736L)]
    public void For_MapsCpuToNanoCpusAndMemoryMbToBytes(int cpu, int memoryMb, long expectedNanoCpus, long expectedBytes)
    {
        var instance = NewInstance(InstanceEngine.Postgres, "16", cpu, memoryMb);

        var spec = DockerContainerSpec.For(instance, Images.Resolve(instance.Engine, instance.Version), "s3cret", "aurora-db");

        Assert.Equal(expectedNanoCpus, spec.NanoCpus);
        Assert.Equal(expectedBytes, spec.MemoryBytes);
    }

    [Fact]
    public void ToString_DoesNotRevealThePassword()
    {
        var instance = NewInstance(InstanceEngine.Postgres, "16");

        var spec = DockerContainerSpec.For(instance, Images.Resolve(instance.Engine, instance.Version), "s3cret", "aurora-db");

        Assert.DoesNotContain("s3cret", spec.ToString());
    }
}
