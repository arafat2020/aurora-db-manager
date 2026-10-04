using AuroraDbManager.Api.Application.Monitoring;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Api.Controllers;

[ApiController]
[Route("api/v1/monitoring")]
[Produces("application/json")]
public sealed class MonitoringController(MonitoringSummaryService summary) : ControllerBase
{
    /// <summary>Returns a snapshot of the system's operational state.</summary>
    /// <remarks>
    /// Counts of instances, jobs, backups and restores, the state of the backup scheduler, and
    /// the most recent failures, all read from the server's own records. No instance is inspected
    /// and neither Docker nor the backup storage is asked: for one instance's runtime health use
    /// <c>GET /api/v1/instances/{id}/health</c>, and for the server's own, <c>GET /health/ready</c>.
    /// </remarks>
    [HttpGet("summary")]
    [ProducesResponseType<MonitoringSummaryResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken)
    {
        return Ok(await summary.GetAsync(cancellationToken));
    }
}
