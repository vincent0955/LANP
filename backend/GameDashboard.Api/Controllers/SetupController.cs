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
    private readonly IKubernetesService _kubernetesService;

    public SetupController(IKubernetesService kubernetesService)
    {
        _kubernetesService = kubernetesService;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        var status = await _kubernetesService.GetSetupStatusAsync(ct);
        return Ok(status);
    }

    /// <summary>
    /// Write-only: creates or updates the game-secrets Secret. There is
    /// deliberately no corresponding GET — secret values are never readable back
    /// through this API (Req 13.3, Req 14.3).
    /// </summary>
    [HttpPost("secrets")]
    public async Task<IActionResult> SetSecrets([FromBody] IDictionary<string, string>? values, CancellationToken ct)
    {
        if (values is null || values.Count == 0)
        {
            return Problem(
                title: "Invalid request.", detail: "At least one secret key/value pair is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        await _kubernetesService.SetSecretsAsync(values, ct);
        return NoContent();
    }
}
