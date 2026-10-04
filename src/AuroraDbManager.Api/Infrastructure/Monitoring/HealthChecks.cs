using System.Text.Json;
using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Infrastructure.Backups;
using AuroraDbManager.Api.Infrastructure.Backups.S3;
using AuroraDbManager.Api.Infrastructure.Docker;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Infrastructure.Monitoring;

/// <summary>
/// The health endpoints and what each of them asks.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>/health</c>, liveness: is the process serving requests? It runs no check at all, so
/// nothing outside the process, the system database, Docker or S3, can make it fail.</item>
/// <item><c>/health/ready</c>, readiness: can Aurora do its management work now? The system
/// database is required: without it the answer is <c>unhealthy</c> and 503. Docker is needed to
/// provision and to reach instances, but reads, job status and monitoring work without it, so a
/// Docker that cannot be reached makes the answer <c>degraded</c>, still 200.</item>
/// <item><c>/health/storage</c>, operational, for signed-in users: can the S3 backup storage be used? Never part of
/// readiness: backups may be local, and an S3 outage fails backups, not the API. Not meant for
/// a probe: each call is a request to the object store.</item>
/// </list>
/// A check reports a status and nothing else. Why it failed is in the application's log.
/// </remarks>
public static class HealthEndpoints
{
    public const string LivenessPath = "/health";
    public const string ReadinessPath = "/health/ready";
    public const string StoragePath = "/health/storage";

    public const string MetadataDatabaseCheck = "metadataDatabase";
    public const string DockerCheck = "docker";
    public const string S3Check = "s3";

    /// <summary>What a check that does not apply to this server reports itself as.</summary>
    public const string NotApplicable = "not_applicable";

    internal const string ApplicableKey = "applicable";

    private const string ReadyTag = "ready";
    private const string StorageTag = "storage";

    public static IServiceCollection AddAuroraHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<MetadataDatabaseHealthCheck>(MetadataDatabaseCheck, HealthStatus.Unhealthy, [ReadyTag])
            .AddCheck<DockerHealthCheck>(DockerCheck, HealthStatus.Degraded, [ReadyTag])
            .AddCheck<S3StorageHealthCheck>(S3Check, HealthStatus.Unhealthy, [StorageTag]);
        return services;
    }

    /// <summary>Whether a check is one of those readiness is made of.</summary>
    public static bool IsReadinessCheck(HealthCheckRegistration check) => check.Tags.Contains(ReadyTag);

    public static void MapAuroraHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        // The two probes are public: whatever runs Aurora has to be able to ask them without a
        // user. They return statuses only. The storage check is for operators, and each call is
        // a request to the object store, so it takes a signed-in user like the rest of monitoring.
        endpoints.MapHealthChecks(LivenessPath, Options(_ => false)).AllowAnonymous();
        endpoints.MapHealthChecks(ReadinessPath, Options(check => check.Tags.Contains(ReadyTag))).AllowAnonymous();
        endpoints.MapHealthChecks(StoragePath, Options(check => check.Tags.Contains(StorageTag)))
            .RequireAuthorization(AuroraPolicies.Viewer);
    }

    private static HealthCheckOptions Options(Func<HealthCheckRegistration, bool> predicate) => new()
    {
        Predicate = predicate,
        ResponseWriter = WriteAsync
    };

    /// <summary>
    /// Writes the overall status and each check's status, by name. Descriptions, exceptions and
    /// durations are left out: they can name hosts, paths and other internals.
    /// </summary>
    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        var json = context.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        var body = new HealthResponse(
            StatusText(report.Status),
            report.Entries.ToDictionary(
                entry => entry.Key,
                entry => entry.Value.Data.TryGetValue(ApplicableKey, out var applicable) && applicable is false
                    ? NotApplicable
                    : StatusText(entry.Value.Status)));

        return context.Response.WriteAsJsonAsync(body, json);
    }

    // As every other status in the API: lower case.
    private static string StatusText(HealthStatus status) => JsonNamingPolicy.SnakeCaseLower.ConvertName(status.ToString());
}

/// <param name="Status">The worst status of the checks that ran: <c>healthy</c>, <c>degraded</c> or <c>unhealthy</c>.</param>
/// <param name="Checks">Each check's own status, by name; <c>not_applicable</c> for one that does not apply to this server.</param>
public sealed record HealthResponse(string Status, IReadOnlyDictionary<string, string> Checks);

/// <summary>Whether the system database answers: one <c>SELECT 1</c>, nothing more.</summary>
public sealed class MetadataDatabaseHealthCheck(AppDbContext db) : IHealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT 1", timeout.Token);
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The exception goes to the log with the report, never into the response.
            return new HealthCheckResult(context.Registration.FailureStatus, "The system database cannot be reached.", exception);
        }
    }
}

/// <summary>Whether the Docker Engine answers a ping. No container is listed or inspected, and nothing is changed.</summary>
public sealed class DockerHealthCheck(IDockerEngine docker) : IHealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            await docker.PingAsync(timeout.Token);
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Docker cannot be reached.", exception);
        }
    }
}

/// <summary>
/// Whether the S3 backup storage can be used: one request about the bucket, no object read or
/// written. On a server with no S3 settings the check does not apply, and says so instead of failing.
/// </summary>
public sealed class S3StorageHealthCheck(IS3ObjectClient client, IOptions<BackupOptions> options) : IHealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly IReadOnlyDictionary<string, object> NotApplicableData =
        new Dictionary<string, object> { [HealthEndpoints.ApplicableKey] = false };

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = options.Value.S3;
        if (settings.Validate() is not null)
        {
            return HealthCheckResult.Healthy("S3 backup storage is not configured.", NotApplicableData);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            await client.CheckBucketAsync(settings.Bucket, timeout.Token);
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BackupOperationException exception)
        {
            // The stable code says what kind of failure; the SDK's own message stays in the inner exception.
            return new HealthCheckResult(context.Registration.FailureStatus, exception.Code, exception);
        }
        catch (Exception exception)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, BackupErrorCodes.BackupStorageUnavailable, exception);
        }
    }
}
