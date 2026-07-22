using GameDashboard.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace GameDashboard.Api.Controllers;

/// <summary>
/// World backup/restore (WS1). Backups are game-agnostic snapshots of a
/// server's single data volume, stored as .tar.gz archives on the host.
/// Error responses use ControllerBase.Problem() so the wire shape matches the
/// RFC 7807 ProblemDetails output of ExceptionHandlingMiddleware.
/// </summary>
[ApiController]
[Route("api/servers/{name}/backups")]
public sealed class BackupController : ControllerBase
{
    private readonly IBackupService _backups;

    public BackupController(IBackupService backups)
    {
        _backups = backups;
    }

    [HttpGet]
    public async Task<IActionResult> List(string name, CancellationToken ct)
    {
        if (!ServerNameValidator.IsValid(name))
        {
            return InvalidNameProblem(name);
        }

        var backups = await _backups.ListBackupsAsync(name, ct);
        return Ok(backups);
    }

    [HttpPost]
    public async Task<IActionResult> Create(string name, CancellationToken ct)
    {
        if (!ServerNameValidator.IsValid(name))
        {
            return InvalidNameProblem(name);
        }

        var info = await _backups.CreateBackupAsync(name, ct);
        return Ok(info);
    }

    [HttpPost("{id}/restore")]
    public async Task<IActionResult> Restore(string name, string id, CancellationToken ct)
    {
        if (!ServerNameValidator.IsValid(name))
        {
            return InvalidNameProblem(name);
        }

        await _backups.RestoreBackupAsync(name, id, ct);
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string name, string id, CancellationToken ct)
    {
        if (!ServerNameValidator.IsValid(name))
        {
            return InvalidNameProblem(name);
        }

        await _backups.DeleteBackupAsync(name, id, ct);
        return NoContent();
    }

    [HttpGet("{id}/download")]
    public IActionResult Download(string name, string id)
    {
        if (!ServerNameValidator.IsValid(name))
        {
            return InvalidNameProblem(name);
        }

        var path = _backups.GetBackupFilePath(name, id);
        var stream = System.IO.File.OpenRead(path);
        return File(stream, "application/gzip", $"{name}-{id}.tar.gz");
    }

    private ObjectResult InvalidNameProblem(string name) =>
        Problem(
            title: "Invalid request.",
            detail: $"'{name}' is not a valid server name.",
            statusCode: StatusCodes.Status400BadRequest);
}
