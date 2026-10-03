using System.ComponentModel.DataAnnotations;

namespace AuroraDbManager.Api.Infrastructure.Databases;

/// <summary>Settings for engine-side database operations, bound from the <c>Databases</c> configuration section.</summary>
public sealed class DatabaseManagerOptions
{
    public const string SectionName = "Databases";

    /// <summary>How long connecting to an instance's database server may take.</summary>
    [Range(1, 300)]
    public int ConnectTimeoutSeconds { get; set; } = 10;

    /// <summary>How long a single statement, such as <c>CREATE DATABASE</c>, may take.</summary>
    [Range(1, 3600)]
    public int CommandTimeoutSeconds { get; set; } = 60;
}
