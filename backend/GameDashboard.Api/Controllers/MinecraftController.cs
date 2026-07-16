using GameDashboard.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace GameDashboard.Api.Controllers;

/// <summary>
/// Read-only metadata backing the Minecraft deploy dialog: version lists per
/// server-software type and Modrinth content search (docs/minecraft-server-types.md).
/// Upstream outages map to 503 — the dialog degrades to free-text inputs, and the
/// deploy path itself never depends on these endpoints.
/// </summary>
[ApiController]
[Route("api/minecraft")]
public sealed class MinecraftController : ControllerBase
{
    private readonly IMinecraftMetadataService _metadata;

    public MinecraftController(IMinecraftMetadataService metadata)
    {
        _metadata = metadata;
    }

    [HttpGet("versions")]
    public async Task<IActionResult> GetVersions([FromQuery] string type, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return BadRequest(new ProblemDetails { Title = "Query parameter 'type' is required." });
        }

        try
        {
            return Ok(await _metadata.GetVersionsAsync(type, ct));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails { Title = ex.Message });
        }
        catch (MinecraftMetadataUnavailableException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails { Title = ex.Message });
        }
    }

    [HttpGet("content/search")]
    public async Task<IActionResult> SearchContent(
        [FromQuery] string? q,
        [FromQuery] string kind,
        [FromQuery] string? loader,
        [FromQuery] string? mcVersion,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return BadRequest(new ProblemDetails { Title = "Query parameter 'kind' is required." });
        }

        try
        {
            return Ok(await _metadata.SearchContentAsync(q, kind, loader, mcVersion, ct));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails { Title = ex.Message });
        }
        catch (MinecraftMetadataUnavailableException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails { Title = ex.Message });
        }
    }
}
