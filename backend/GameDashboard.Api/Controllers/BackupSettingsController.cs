using GameDashboard.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace GameDashboard.Api.Controllers;

/// <summary>
/// Read/update the scheduled-backup settings (WS1). Separate from the per-server
/// backup routes because it's a single machine-wide setting, surfaced on the
/// Settings screen.
/// </summary>
[ApiController]
[Route("api/backups/settings")]
public sealed class BackupSettingsController : ControllerBase
{
    private readonly IBackupSettingsStore _store;

    public BackupSettingsController(IBackupSettingsStore store)
    {
        _store = store;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        return Ok(await _store.GetAsync(ct));
    }

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] BackupSettings? settings, CancellationToken ct)
    {
        if (settings is null)
        {
            return Problem(
                title: "Invalid request.", detail: "A settings body is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        await _store.SetAsync(settings, ct);
        return Ok(await _store.GetAsync(ct));
    }
}
