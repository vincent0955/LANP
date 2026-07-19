using GameDashboard.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace GameDashboard.Api.Controllers;

/// <summary>
/// See requirements.md → Req 1.2 (health/readiness endpoint).
/// </summary>
[ApiController]
[Route("api/health")]
public sealed class HealthController : ControllerBase
{
    private readonly IServerOrchestrator _orchestrator;

    public HealthController(IServerOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    [HttpGet]
    public async Task<IActionResult> GetHealth(CancellationToken ct)
    {
        var health = await _orchestrator.GetHealthAsync(ct);

        // The endpoint itself always returns 200 — health is reported in the body,
        // not via HTTP status, so the dashboard can render "engine unreachable"
        // as a normal state rather than treating it as an API failure.
        return Ok(health);
    }
}
