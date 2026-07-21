using GameDashboard.Api.Hubs;
using GameDashboard.Api.Models;
using GameDashboard.Api.Services.Docker;
using Microsoft.AspNetCore.SignalR;

namespace GameDashboard.Api.RealTime;

/// <summary>
/// Replaces the Kubernetes PodWatchService: Docker has an events API, but a
/// 3-second list+diff poll is simpler, covers the TCP-probe-driven
/// Pending→Running transition (which no engine event fires for), and is
/// near-free against a local engine (docs/docker-migration.md → concept
/// mapping). Pushes <see cref="ServerStatusChangedMessage"/> to SignalR
/// clients in the "events" group whenever a server's derived status changes.
/// </summary>
public sealed class ContainerWatchService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly IDockerClientFactory _clientFactory;
    private readonly DeployTracker _deployTracker;
    private readonly ITcpReadinessProber _prober;
    private readonly IHubContext<DashboardHub> _hubContext;
    private readonly ILogger<ContainerWatchService> _logger;

    // Last known status per server, so we only emit on actual changes.
    private readonly Dictionary<string, ServerStatus> _lastKnownStatus = new(StringComparer.Ordinal);

    public ContainerWatchService(
        IDockerClientFactory clientFactory,
        DeployTracker deployTracker,
        ITcpReadinessProber prober,
        IHubContext<DashboardHub> hubContext,
        ILogger<ContainerWatchService> logger)
    {
        _clientFactory = clientFactory;
        _deployTracker = deployTracker;
        _prober = prober;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (await WaitForNextTickSafeAsync(timer, stoppingToken))
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Engine down mid-poll is a normal condition (bundled runtime
                // still booting, Docker Desktop restarting); just try again.
                _logger.LogDebug(ex, "Container watch poll skipped.");
            }
        }
    }

    private static async Task<bool> WaitForNextTickSafeAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var (client, _) = await _clientFactory.TryGetClientAsync(ct);
        if (client is null)
        {
            return;
        }

        var containers = await client.Containers.ListContainersAsync(new global::Docker.DotNet.Models.ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool> { [ContainerLabels.ManagedFilter] = true },
            },
        }, ct);

        var current = new Dictionary<string, ServerStatus>(StringComparer.Ordinal);

        foreach (var container in containers)
        {
            var name = container.Names?.FirstOrDefault()?.TrimStart('/') ?? container.ID[..12];
            var ports = PortsOf(container.Labels);
            var ready = await _prober.IsReadyAsync(container.ID, container.State, ports, ct);
            var deployInFlight = _deployTracker.Get(name) is { Failed: false };
            current[name] = ContainerStatusMapper.Map(container.State, ready, deployInFlight);
        }

        foreach (var pending in _deployTracker.All)
        {
            if (!current.ContainsKey(pending.Name))
            {
                current[pending.Name] = pending.Failed ? ServerStatus.Error : ServerStatus.Pending;
            }
        }

        List<(string Name, ServerStatus Status)> changes = new();
        lock (_lastKnownStatus)
        {
            foreach (var (name, status) in current)
            {
                if (!_lastKnownStatus.TryGetValue(name, out var previous) || previous != status)
                {
                    _lastKnownStatus[name] = status;
                    changes.Add((name, status));
                }
            }

            // Forget deleted servers so a redeploy under the same name emits again.
            foreach (var gone in _lastKnownStatus.Keys.Where(k => !current.ContainsKey(k)).ToList())
            {
                _lastKnownStatus.Remove(gone);
            }
        }

        foreach (var (name, status) in changes)
        {
            var message = new ServerStatusChangedMessage(name, status, DateTimeOffset.UtcNow);
            await _hubContext.Clients.Group("events").SendAsync(HubEvents.ServerStatusChanged, message, ct);
            _logger.LogDebug("Server {Server} status changed to {Status}.", name, status);
        }
    }

    private static IReadOnlyList<PortMapping> PortsOf(IDictionary<string, string>? labels)
    {
        if (labels is null || !labels.TryGetValue(ContainerLabels.Ports, out var json))
        {
            return Array.Empty<PortMapping>();
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<PortMapping>>(json)
                ?? (IReadOnlyList<PortMapping>)Array.Empty<PortMapping>();
        }
        catch (System.Text.Json.JsonException)
        {
            return Array.Empty<PortMapping>();
        }
    }
}
