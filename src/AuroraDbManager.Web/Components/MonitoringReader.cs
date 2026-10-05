using AuroraDbManager.Api.Application.Monitoring;
using AuroraDbManager.Api.Infrastructure.Monitoring;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AuroraDbManager.Web.Components;

/// <summary>
/// What the monitoring page shows, read from what monitoring already provides and from nothing
/// else: the summary behind <c>/api/v1/monitoring/summary</c>, and the health checks behind
/// <c>/health/ready</c> and <c>/health/storage</c>, run through the application's own
/// <see cref="HealthCheckService"/>. No check is made here, and nothing is counted here.
/// </summary>
public sealed class MonitoringReader(MonitoringSummaryService summary, HealthCheckService health)
{
    public async Task<MonitoringView> ReadAsync(CancellationToken cancellationToken)
    {
        // Every registered check: the ones readiness is made of, and the backup storage's.
        var report = await health.CheckHealthAsync(cancellationToken);
        var readiness = await health.CheckHealthAsync(HealthEndpoints.IsReadinessCheck, cancellationToken);

        return new MonitoringView(
            await summary.GetAsync(cancellationToken),
            readiness.Status,
            report.Entries
                .Select(entry => new HealthCheckView(
                    entry.Key,
                    entry.Value.Status,
                    // A check that does not apply to this server says so, in the terms the health endpoint uses.
                    Applies: !(entry.Value.Data.TryGetValue("applicable", out var applicable) && applicable is false)))
                .ToList());
    }
}

/// <param name="Summary">The monitoring summary.</param>
/// <param name="Readiness">Whether Aurora can do its work now: the answer of <c>/health/ready</c>.</param>
/// <param name="Checks">Every health check, by the name the health endpoints use.</param>
public sealed record MonitoringView(MonitoringSummaryResponse Summary, HealthStatus Readiness, IReadOnlyList<HealthCheckView> Checks);

/// <param name="Name">The check's name in the health endpoints.</param>
/// <param name="Status">What it reported. Only that: why is in the server's log.</param>
/// <param name="Applies">False if the check has nothing to check on this server.</param>
public sealed record HealthCheckView(string Name, HealthStatus Status, bool Applies)
{
    public string Label => Name switch
    {
        HealthEndpoints.MetadataDatabaseCheck => "System database",
        HealthEndpoints.DockerCheck => "Docker",
        HealthEndpoints.S3Check => "S3 backup storage",
        _ => Name
    };

    public string Explanation => Name switch
    {
        HealthEndpoints.MetadataDatabaseCheck => "Where Aurora keeps what it knows. Nothing works without it.",
        HealthEndpoints.DockerCheck => "Needed to provision instances and to reach them. Reading, job status and monitoring work without it.",
        HealthEndpoints.S3Check => "Where backups in S3 are kept. Not part of readiness: backups may be local.",
        _ => string.Empty
    };
}
