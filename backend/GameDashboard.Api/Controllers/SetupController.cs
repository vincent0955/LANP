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
    /// Creates or updates the game-secrets Secret. There is deliberately no bulk
    /// GET — values only leave the cluster one at a time via the explicit
    /// per-key reveal endpoint below (relaxation of Req 13.3 / Req 14.3).
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

    /// <summary>
    /// Reveals a single secret value for the setup page's click-to-show button.
    /// 404 when the key (or the Secret itself) doesn't exist.
    /// </summary>
    [HttpGet("secrets/{key}/value")]
    public async Task<IActionResult> GetSecretValue(string key, CancellationToken ct)
    {
        var value = await _kubernetesService.GetSecretValueAsync(key, ct);
        return Ok(new { value });
    }

    /// <summary>
    /// Removes a single key from the game-secrets Secret. Keys referenced by a
    /// curated game template are rejected with 409 (see
    /// KubernetesService.DeleteSecretKeyAsync).
    /// </summary>
    [HttpDelete("secrets/{key}")]
    public async Task<IActionResult> DeleteSecretKey(string key, CancellationToken ct)
    {
        await _kubernetesService.DeleteSecretKeyAsync(key, ct);
        return NoContent();
    }
}
