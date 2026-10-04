using AuroraDbManager.Api.Application.Monitoring;
using AuroraDbManager.Api.Infrastructure.Monitoring;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AuroraDbManager.Web.Components;

/// <summary>
/// What the overview shows, read from what monitoring already provides: the summary the API
/// serves at <c>/api/v1/monitoring/summary</c> and the readiness checks behind
/// <c>/health/ready</c>. Nothing is counted or checked here that those do not already.
/// </summary>
public sealed class DashboardReader(MonitoringSummaryService summary, HealthCheckService health)
{
    public async Task<Dashboard> ReadAsync(CancellationToken cancellationToken)
    {
        var readiness = await health.CheckHealthAsync(HealthEndpoints.IsReadinessCheck, cancellationToken);
        return new Dashboard(
            await summary.GetAsync(cancellationToken),
            readiness.Status,
            readiness.Entries.ToDictionary(entry => entry.Key, entry => entry.Value.Status));
    }
}

/// <param name="Summary">The monitoring summary.</param>
/// <param name="Readiness">Whether Aurora can do its work now.</param>
/// <param name="Dependencies">The dependencies that readiness is made of, by the names the health endpoint uses.</param>
public sealed record Dashboard(
    MonitoringSummaryResponse Summary,
    HealthStatus Readiness,
    IReadOnlyDictionary<string, HealthStatus> Dependencies)
{
    /// <summary>The names as a person reads them.</summary>
    public static string DependencyLabel(string check) => check switch
    {
        HealthEndpoints.MetadataDatabaseCheck => "System database",
        HealthEndpoints.DockerCheck => "Docker",
        _ => check
    };
}
