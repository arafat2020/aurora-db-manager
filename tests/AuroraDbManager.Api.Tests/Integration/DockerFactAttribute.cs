namespace AuroraDbManager.Api.Tests.Integration;

/// <summary>
/// Marks a test that creates real containers. It is skipped unless <c>AURORA_DOCKER_TESTS=1</c>,
/// so a plain <c>dotnet test</c> never needs Docker.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DockerFactAttribute : FactAttribute
{
    public const string EnableVariable = "AURORA_DOCKER_TESTS";

    public DockerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnableVariable) != "1")
        {
            Skip = $"Docker integration test; set {EnableVariable}=1 to run it against the local Docker daemon.";
        }
    }
}
