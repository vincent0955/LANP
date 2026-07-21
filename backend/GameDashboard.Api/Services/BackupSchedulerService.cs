using GameDashboard.Api.Hubs;
using GameDashboard.Api.Models;
using GameDashboard.Api.RealTime;
using Microsoft.AspNetCore.SignalR;

namespace GameDashboard.Api.Services;

/// <summary>
/// Scheduled auto-backups (WS1). On a fixed evaluation tick it re-reads the
/// user-editable <see cref="BackupSettings"/> (so toggling the schedule from the
/// Settings screen takes effect without a restart) and, when a backup is due,
/// snapshots every real server's data volume and prunes each server's archives
/// to the configured retention count. Follows the <see cref="AutoScaleService"/>
/// hosted-service pattern (singleton loop, a fresh DI scope per run).
/// </summary>
public sealed class BackupSchedulerService : BackgroundService
{
    /// <summary>How often the loop re-checks whether a backup is due. Independent of the backup interval.</summary>
    private static readonly TimeSpan EvaluationTick = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBackupSettingsStore _settingsStore;
    private readonly IHubContext<DashboardHub> _hubContext;
    private readonly ILogger<BackupSchedulerService> _logger;

    private DateTimeOffset? _lastRun;

    public BackupSchedulerService(
        IServiceScopeFactory scopeFactory,
        IBackupSettingsStore settingsStore,
        IHubContext<DashboardHub> hubContext,
        ILogger<BackupSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _settingsStore = settingsStore;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(EvaluationTick, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await EvaluateAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled backup evaluation failed; will retry next tick.");
            }
        }
    }

    private async Task EvaluateAsync(CancellationToken ct)
    {
        var settings = await _settingsStore.GetAsync(ct);
        if (!settings.Enabled)
        {
            // Disabling the schedule also resets the baseline, so re-enabling
            // waits a full interval rather than firing immediately.
            _lastRun = null;
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, settings.IntervalMinutes));
        var now = DateTimeOffset.UtcNow;

        // Establish a baseline on first enable rather than backing everything up
        // the instant the app launches.
        if (_lastRun is null)
        {
            _lastRun = now;
            return;
        }

        if (now - _lastRun < interval)
        {
            return;
        }

        _lastRun = now;
        await RunOnceAsync(settings.RetentionCount, ct);
    }

    private async Task RunOnceAsync(int retentionCount, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IServerOrchestrator>();
        var backupService = scope.ServiceProvider.GetRequiredService<IBackupService>();

        var servers = await orchestrator.ListServersAsync(ct);

        // Only servers with a real container/volume are backable; a purely
        // Pending (still-deploying) entry has no volume yet.
        var targets = servers
            .Where(s => s.Status is ServerStatus.Running or ServerStatus.Stopped or ServerStatus.Error)
            .Select(s => s.Name)
            .ToList();

        _logger.LogInformation("Running scheduled backup for {Count} server(s).", targets.Count);

        foreach (var name in targets)
        {
            try
            {
                var info = await backupService.CreateBackupAsync(name, ct);
                await PruneAsync(backupService, name, retentionCount, ct);

                await _hubContext.Clients.Group("events").SendAsync(
                    HubEvents.BackupCompleted,
                    new BackupCompletedMessage(name, info.Id, info.SizeBytes, DateTimeOffset.UtcNow),
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled backup of server {Server} failed.", name);
            }
        }
    }

    private async Task PruneAsync(
        IBackupService backupService, string serverName, int retentionCount, CancellationToken ct)
    {
        if (retentionCount <= 0)
        {
            return; // retention disabled — keep everything
        }

        var backups = await backupService.ListBackupsAsync(serverName, ct); // newest first
        foreach (var stale in backups.Skip(retentionCount))
        {
            try
            {
                await backupService.DeleteBackupAsync(serverName, stale.Id, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to prune old backup {Id} for {Server}.", stale.Id, serverName);
            }
        }
    }
}
