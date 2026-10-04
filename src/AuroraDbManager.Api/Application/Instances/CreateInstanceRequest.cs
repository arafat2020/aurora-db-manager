using System.ComponentModel.DataAnnotations;
using AuroraDbManager.Api.Domain.Instances;

namespace AuroraDbManager.Api.Application.Instances;

public sealed class CreateInstanceRequest
{
    // Far more than any host has, and far less than what turns into nonsense further down: a
    // request is for resources, not for whatever number fits the field.
    public const int MaxCpu = 256;
    public const int MaxMemoryMb = 1024 * 1024;
    public const int MaxStorageGb = 65_536;

    /// <summary>Display name of the instance. Required, at most 100 characters, none of them control characters.</summary>
    [Required(ErrorMessage = "name is required.")]
    // A display name is shown and listed as it is; a line break or an escape sequence in it is not a name.
    [RegularExpression(@"^[^\p{Cc}]*$", ErrorMessage = "name must not contain control characters.")]
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

    /// <summary>Number of CPU cores. Required, between 1 and 256.</summary>
    [Required(ErrorMessage = "cpu is required.")]
    [Range(1, MaxCpu, ErrorMessage = "cpu must be between {1} and {2}.")]
    public int? Cpu { get; init; }

    /// <summary>Memory in megabytes. Required, between 1 and 1048576 (1 TiB).</summary>
    [Required(ErrorMessage = "memoryMb is required.")]
    [Range(1, MaxMemoryMb, ErrorMessage = "memoryMb must be between {1} and {2}.")]
    public int? MemoryMb { get; init; }

    /// <summary>Storage in gigabytes. Required, between 1 and 65536.</summary>
    [Required(ErrorMessage = "storageGb is required.")]
    [Range(1, MaxStorageGb, ErrorMessage = "storageGb must be between {1} and {2}.")]
    public int? StorageGb { get; init; }
}
