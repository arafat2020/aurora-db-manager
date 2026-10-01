using System.ComponentModel.DataAnnotations;

namespace AuroraDbManager.Api.Application.Jobs;

/// <summary>Settings for background job processing, bound from the <c>Jobs</c> configuration section.</summary>
public sealed class JobOptions
{
    public const string SectionName = "Jobs";

    /// <summary>Maximum number of jobs processed at the same time.</summary>
    [Range(1, 64)]
    public int MaxConcurrency { get; set; } = 2;

    /// <summary>Attempts a new job gets before it is marked failed.</summary>
    [Range(1, 20)]
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Wait between a failed attempt and the next one.</summary>
    [Range(0, 3600)]
    public int RetryDelaySeconds { get; set; } = 1;
}
