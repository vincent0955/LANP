using GameDashboard.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace GameDashboard.Api.Controllers;

/// <summary>
/// See requirements.md → Req 9 (resource metrics).
/// </summary>
[ApiController]
[Route("api/metrics")]
public sealed class MetricsController : ControllerBase
{
    private readonly IMetricsService _metricsService;

    public MetricsController(IMetricsService metricsService)
    {
        _metricsService = metricsService;
    }

    [HttpGet]
    public async Task<IActionResult> GetMetrics(CancellationToken ct)
    {
        // Always 200: unavailability is communicated in the body (Available=false),
        // consistent with the /api/health design (Req 9.3).
        var snapshot = await _metricsService.GetSnapshotAsync(ct);
        return Ok(snapshot);
    }
}
