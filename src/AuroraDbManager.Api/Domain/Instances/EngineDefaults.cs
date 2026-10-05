namespace AuroraDbManager.Api.Domain.Instances;

/// <summary>What is the same for every server of an engine, whatever its version: the facts a client needs to connect.</summary>
public static class EngineDefaults
{
    /// <summary>The TCP port the engine's server listens on inside its container.</summary>
    public static int Port(InstanceEngine engine) => engine switch
    {
        InstanceEngine.Postgres => 5432,
        InstanceEngine.Mysql => 3306,
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, null)
    };

    /// <summary>The administrator account the engine's image creates, the one an instance's stored password belongs to.</summary>
    public static string AdminUser(InstanceEngine engine) => engine switch
    {
        InstanceEngine.Postgres => "postgres",
        InstanceEngine.Mysql => "root",
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, null)
    };

    /// <summary>The scheme of the engine's connection URIs.</summary>
    public static string UriScheme(InstanceEngine engine) => engine switch
    {
        InstanceEngine.Postgres => "postgresql",
        InstanceEngine.Mysql => "mysql",
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, null)
    };
}
