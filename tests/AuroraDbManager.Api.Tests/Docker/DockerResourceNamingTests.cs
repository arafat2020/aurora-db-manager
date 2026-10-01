using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Infrastructure.Docker;

namespace AuroraDbManager.Api.Tests.Docker;

public sealed class DockerResourceNamingTests
{
    private static readonly Guid InstanceId = Guid.Parse("01a0f6b8-af85-7cd2-8d02-fac004008801");

    [Fact]
    public void Names_DeriveFromTheInstanceId()
    {
        Assert.Equal("aurora-instance-01a0f6b8-af85-7cd2-8d02-fac004008801", DockerResourceNaming.ContainerName(InstanceId));
        Assert.Equal("aurora-instance-01a0f6b8-af85-7cd2-8d02-fac004008801-data", DockerResourceNaming.VolumeName(InstanceId));
    }

    [Fact]
    public void Names_AreTheSameEveryTime_AndDifferentPerInstance()
    {
        Assert.Equal(DockerResourceNaming.ContainerName(InstanceId), DockerResourceNaming.ContainerName(InstanceId));
        Assert.Equal(DockerResourceNaming.VolumeName(InstanceId), DockerResourceNaming.VolumeName(InstanceId));
        Assert.NotEqual(DockerResourceNaming.ContainerName(InstanceId), DockerResourceNaming.ContainerName(Guid.NewGuid()));
    }

    [Fact]
    public void Names_DoNotContainTheUserChosenInstanceName()
    {
        var instance = Instance.Create("my prod db; rm -rf /", InstanceEngine.Postgres, "16", 1, 1024, 20, DateTime.UtcNow);

        var container = DockerResourceNaming.ContainerName(instance.Id);

        Assert.Equal($"aurora-instance-{instance.Id}", container);
        Assert.DoesNotContain("prod", container);
    }

    [Fact]
    public void InstanceLabels_IdentifyTheOwner()
    {
        var labels = DockerResourceNaming.InstanceLabels(InstanceId);

        Assert.Equal("true", labels["aurora.managed"]);
        Assert.Equal(InstanceId.ToString(), labels["aurora.instance-id"]);
        Assert.True(DockerResourceNaming.IsOwnedBy(labels, InstanceId));
        Assert.False(DockerResourceNaming.IsOwnedBy(labels, Guid.NewGuid()));
        Assert.False(DockerResourceNaming.IsOwnedBy(new Dictionary<string, string>(), InstanceId));
    }
}
