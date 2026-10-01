using System.ComponentModel.DataAnnotations;
using AuroraDbManager.Api.Domain.Instances;

namespace AuroraDbManager.Api.Application.Instances;

public sealed class CreateInstanceRequest
{
    /// <summary>Display name of the instance. Required, at most 100 characters.</summary>
    [Required(ErrorMessage = "name is required.")]
    [MaxLength(Instance.NameMaxLength, ErrorMessage = "name must be at most {1} characters.")]
    public string? Name { get; init; }

    /// <summary>Database engine. Required. Supported values: <c>postgres</c>, <c>mysql</c>.</summary>
    // Bound as a string so an unsupported engine yields a normal validation error
    // instead of a JSON deserialization failure. Keep in sync with InstanceEngine.
    [Required(ErrorMessage = "engine is required.")]
    [AllowedValues("postgres", "mysql", ErrorMessage = "engine must be one of: postgres, mysql.")]
    public string? Engine { get; init; }

    /// <summary>Engine version, e.g. <c>16</c>. Required, at most 32 characters.</summary>
    [Required(ErrorMessage = "version is required.")]
    [MaxLength(Instance.VersionMaxLength, ErrorMessage = "version must be at most {1} characters.")]
    public string? Version { get; init; }

    /// <summary>Number of CPU cores. Required, must be greater than zero.</summary>
    [Required(ErrorMessage = "cpu is required.")]
    [Range(1, int.MaxValue, ErrorMessage = "cpu must be greater than zero.")]
    public int? Cpu { get; init; }

    /// <summary>Memory in megabytes. Required, must be greater than zero.</summary>
    [Required(ErrorMessage = "memoryMb is required.")]
    [Range(1, int.MaxValue, ErrorMessage = "memoryMb must be greater than zero.")]
    public int? MemoryMb { get; init; }

    /// <summary>Storage in gigabytes. Required, must be greater than zero.</summary>
    [Required(ErrorMessage = "storageGb is required.")]
    [Range(1, int.MaxValue, ErrorMessage = "storageGb must be greater than zero.")]
    public int? StorageGb { get; init; }
}
