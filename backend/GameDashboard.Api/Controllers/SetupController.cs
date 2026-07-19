using GameDashboard.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace GameDashboard.Api.Controllers;

/// <summary>
/// See requirements.md → Req 13 (first-run setup and plug-and-play).
/// </summary>
[ApiController]
[Route("api/setup")]
public sealed class SetupController : ControllerBase
{
    private readonly IServerOrchestrator _orchestrator;

    public SetupController(IServerOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        var status = await _orchestrator.GetSetupStatusAsync(ct);
        return Ok(status);
    }
}
