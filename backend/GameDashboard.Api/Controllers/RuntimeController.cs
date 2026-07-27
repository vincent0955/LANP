using GameDashboard.Api.Exceptions;
using GameDashboard.Api.Models;
using GameDashboard.Api.Services;
using GameDashboard.Api.Services.Runtime;
using Microsoft.AspNetCore.Mvc;

namespace GameDashboard.Api.Controllers;

/// <summary>
/// Bundled container runtime management (docs/docker-migration.md → Part 2).
/// The Setup screen drives the Windows first-run flow through these endpoints;
/// on Linux, status simply reports engine reachability.
/// </summary>
[ApiController]
[Route("api/runtime")]
public sealed class RuntimeController : ControllerBase
{
    private readonly IRuntimeSetupService _runtime;
    private readonly IRuntimeModeStore _modeStore;
    private readonly IServerOrchestrator _orchestrator;
    private readonly ILogger<RuntimeController> _logger;

    public RuntimeController(
        IRuntimeSetupService runtime,
        IRuntimeModeStore modeStore,
        IServerOrchestrator orchestrator,
        ILogger<RuntimeController> logger)
    {
        _runtime = runtime;
        _modeStore = modeStore;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        return Ok(await _runtime.GetStatusAsync(ct));
    }

    /// <summary>
    /// Switches the container engine between the app-managed bundled runtime and
    /// a Docker Desktop the user already runs. Persisted, so every subsequent
    /// launch honours it. The choice only takes full effect after the app
    /// restarts (the response says so) — the backend's engine connection,
    /// auto-start and shutdown behaviour are all decided at boot.
    /// </summary>
    [HttpPut("mode")]
    public async Task<IActionResult> SetMode([FromBody] SetRuntimeModeRequest request, CancellationToken ct)
    {
        var current = await _modeStore.GetAsync(ct);
        if (current == request.Mode)
        {
            return Ok(new { mode = current, restartRequired = false });
        }

        // Servers keep running on whichever engine hosts them; they simply stop
        // being visible to a dashboard now pointed elsewhere. Say so plainly
        // rather than silently orphaning them.
        var runningElsewhere = 0;
        try
        {
            var servers = await _orchestrator.ListServersAsync(ct);
            runningElsewhere = servers.Count(s => s.Status is ServerStatus.Running or ServerStatus.Pending);
        }
        catch (ClusterUnreachableException)
        {
            // Current engine is already down — nothing to strand.
        }

        await _modeStore.SetAsync(request.Mode, ct);
        _logger.LogInformation("Container engine switched from {Old} to {New}.", current, request.Mode);

        return Ok(new { mode = request.Mode, restartRequired = true, runningServers = runningElsewhere });
    }

    /// <summary>
    /// Starts the background install (WSL feature → distro download/import →
    /// engine start). 202 when started; 409 when one is already running or the
    /// platform has no bundled runtime. Progress is polled via GET status.
    /// </summary>
    [HttpPost("install")]
    public async Task<IActionResult> StartInstall(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Problem(
                title: "No bundled runtime on this platform.",
                detail: "On Linux, install the native engine with scripts/runtime/install-linux.sh.",
                statusCode: StatusCodes.Status409Conflict);
        }

        if (await _modeStore.GetAsync(ct) == RuntimeMode.DockerDesktop)
        {
            return Problem(
                title: "The container engine is set to Docker Desktop.",
                detail: "Switch back to the bundled runtime on the Setup screen before installing it.",
                statusCode: StatusCodes.Status409Conflict);
        }

        if (!_runtime.TryStartInstall())
        {
            return Problem(
                title: "Install already running.",
                detail: "Poll GET /api/runtime/status for progress.",
                statusCode: StatusCodes.Status409Conflict);
        }

        return Accepted();
    }

    /// <summary>
    /// Full teardown for the tray menu's Exit: gracefully stops every running
    /// game server (30s save window each, in parallel), then shuts the
    /// bundled runtime VM down. Deliberately best-effort — Exit must always
    /// complete, so an unreachable engine just skips straight to VM teardown.
    /// </summary>
    [HttpPost("shutdown")]
    public async Task<IActionResult> Shutdown(CancellationToken ct)
    {
        try
        {
            var servers = await _orchestrator.ListServersAsync(ct);
            var running = servers
                .Where(s => s.Status is ServerStatus.Running or ServerStatus.Pending)
                .Select(s => s.Name)
                .ToList();

            if (running.Count > 0)
            {
                _logger.LogInformation(
                    "App exit: gracefully stopping {Count} running server(s): {Names}.",
                    running.Count, string.Join(", ", running));
                await Task.WhenAll(running.Select(async name =>
                {
                    try
                    {
                        await _orchestrator.ScaleServerAsync(name, 0, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to stop server {Name} during shutdown.", name);
                    }
                }));
            }
        }
        catch (ClusterUnreachableException)
        {
            // Engine already down — nothing to save, proceed to VM teardown.
        }

        await _runtime.StopRuntimeAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Enables WSL mirrored networking in the per-user .wslconfig (merge-edit;
    /// other settings preserved). The response says whether a `wsl --shutdown`
    /// is still needed — the UI asks the user first, since that restarts the VM
    /// their servers run in.
    /// </summary>
    [HttpPost("networking/mirrored")]
    public async Task<IActionResult> EnableMirroredNetworking(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Problem(
                title: "Not applicable on this platform.",
                detail: "Mirrored networking is a WSL (Windows) concern; Linux binds host ports natively.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var (applied, shutdownRequired) = await _runtime.ApplyMirroredNetworkingAsync(ct);
        return Ok(new { applied, shutdownRequired });
    }
}

/// <summary>Body of PUT /api/runtime/mode.</summary>
public sealed record SetRuntimeModeRequest(RuntimeMode Mode);
