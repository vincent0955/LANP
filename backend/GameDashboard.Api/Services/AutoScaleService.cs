using GameDashboard.Api.Configuration;
using GameDashboard.Api.Hubs;
using GameDashboard.Api.Models;
using GameDashboard.Api.RealTime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace GameDashboard.Api.Services;

/// <summary>
/// Background service implementing automatic resource-based scaling.
/// See design.md → AutoScaleService; requirements.md → Req 12.
///
/// Orchestration only — the actual scale-down decision is delegated to
/// <see cref="AutoScaleDecisionEngine"/> (pure logic, unit-tested independently).
/// This class's job is gathering the inputs (metrics, running servers, player
/// presence, last-active times), calling the engine, and carrying out its decision
/// (scaling down, updating last-active annotations, logging, emitting events).
/// </summary>
public sealed class AutoScaleService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<DashboardHub> _hubContext;
    private readonly DashboardOptions _options;
    private readonly ILogger<AutoScaleService> _logger;

    public AutoScaleService(
        IServiceScopeFactory scopeFactory,
        IHubContext<DashboardHub> hubContext,
        IOptions<DashboardOptions> options,
        ILogger<AutoScaleService> logger)
    {
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.AutoScale.Enabled)
        {
            _logger.LogInformation("Auto-scale is disabled via configuration; background loop will not run.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.AutoScale.IntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EvaluateOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto-scale evaluation failed; will retry next interval.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task EvaluateOnceAsync(CancellationToken ct)
    {
        // Scoped services (IServerOrchestrator is scoped) need a scope per iteration
        // since this background service itself is a singleton.
        using var scope = _scopeFactory.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IServerOrchestrator>();
        var metricsService = scope.ServiceProvider.GetRequiredService<IMetricsService>();
        var rconService = scope.ServiceProvider.GetRequiredService<IRconService>();

        var servers = await orchestrator.ListServersAsync(ct);
        var runningServers = servers.Where(s => s.Status == ServerStatus.Running).ToList();

        if (runningServers.Count == 0)
        {
            return;
        }

        var candidates = new List<ServerScaleCandidate>(runningServers.Count);
        foreach (var server in runningServers)
        {
            var playerInfo = await rconService.QueryPlayerInfoAsync(server.Name, ct);
            // A server whose player count cannot be determined (RCON unreachable)
            // is treated as having active players — never scaled down on missing
            // data (Req 12.3: safety-first when uncertain).
            var hasPlayers = playerInfo is null || playerInfo.CurrentPlayers > 0;

            if (hasPlayers)
            {
                await orchestrator.SetLastActiveAsync(server.Name, DateTimeOffset.UtcNow, ct);
            }

            var lastActive = await orchestrator.GetLastActiveAsync(server.Name, ct);
            candidates.Add(new ServerScaleCandidate(server.Name, hasPlayers, lastActive));
        }

        var metrics = await metricsService.GetSnapshotAsync(ct);

        var decision = AutoScaleDecisionEngine.Decide(
            metricsAvailable: metrics.Available,
            memoryUsedBytes: metrics.Node?.MemUsedBytes ?? 0,
            memoryCapacityBytes: metrics.Node?.MemCapacityBytes ?? 0,
            memoryHighWaterPercent: _options.AutoScale.MemoryHighWaterPercent,
            runningServers: candidates);

        _logger.LogDebug("Auto-scale evaluation: {Reason}", decision.Reason);

        foreach (var serverName in decision.ServersToScaleDown)
        {
            try
            {
                await orchestrator.ScaleServerAsync(serverName, 0, ct);
                _logger.LogInformation(
                    "Auto-scaled down server {Server}: {Reason}", serverName, decision.Reason);

                await _hubContext.Clients.Group("events").SendAsync(
                    HubEvents.AutoScaleAction,
                    new AutoScaleActionMessage(serverName, "ScaleDown", decision.Reason, DateTimeOffset.UtcNow),
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-scale down server {Server}.", serverName);
            }
        }
    }
}
