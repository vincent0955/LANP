using GameDashboard.Api.Configuration;
using GameDashboard.Api.Hubs;
using GameDashboard.Api.Models;
using GameDashboard.Api.Services;
using k8s;
using k8s.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace GameDashboard.Api.RealTime;

/// <summary>
/// Background service that watches pods in the game-servers namespace and pushes
/// <see cref="ServerStatusChangedMessage"/> events to SignalR clients subscribed to
/// the "events" group whenever a pod's derived server status changes.
/// See requirements.md → Req 8; design.md → PodWatchService.
///
/// The Kubernetes watch API delivers a long-lived HTTP stream that can be dropped by
/// the API server, network blips, or Docker Desktop restarts. This service treats
/// that as a normal, expected condition and reconnects with a backoff rather than
/// letting the background service fault and stop silently.
/// </summary>
public sealed class PodWatchService : BackgroundService
{
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly IKubernetesClientFactory _clientFactory;
    private readonly IHubContext<DashboardHub> _hubContext;
    private readonly DashboardOptions _options;
    private readonly ILogger<PodWatchService> _logger;

    // Tracks the last known status per server so we only emit an event when the
    // *derived* status actually changes, not on every raw pod event (a pod can emit
    // many MODIFIED events - e.g. resource version bumps - without its Running/Ready
    // state changing).
    private readonly Dictionary<string, ServerStatus> _lastKnownStatus = new();

    public PodWatchService(
        IKubernetesClientFactory clientFactory,
        IHubContext<DashboardHub> hubContext,
        IOptions<DashboardOptions> options,
        ILogger<PodWatchService> logger)
    {
        _clientFactory = clientFactory;
        _hubContext = hubContext;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryDelay = InitialRetryDelay;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WatchOnceAsync(stoppingToken);
                // A clean return means the watch stream ended normally (e.g. server
                // closed it); reset backoff and reconnect promptly.
                retryDelay = InitialRetryDelay;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Pod watch dropped; reconnecting in {Delay}s.", retryDelay.TotalSeconds);
            }

            try
            {
                await Task.Delay(retryDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, MaxRetryDelay.TotalSeconds));
        }
    }

    private async Task WatchOnceAsync(CancellationToken ct)
    {
        if (!_clientFactory.TryGetClient(out var client, out var error))
        {
            // Cluster not reachable yet (e.g. Docker Desktop still starting up).
            // Surface as a normal retry condition rather than a hard failure.
            throw new InvalidOperationException($"Cluster unreachable: {error}");
        }

        var listResponse = client!.CoreV1.ListNamespacedPodWithHttpMessagesAsync(
            _options.Namespace, watch: true, cancellationToken: ct);

#pragma warning disable CS0618 // WatcherExt.Watch is marked obsolete in KubernetesClient 19.x
        // with no documented drop-in replacement at this version; this is the
        // supported pattern for consuming a Kubernetes watch stream today.
        using var watcher = listResponse.Watch<V1Pod, V1PodList>(
            onEvent: (type, pod) => _ = HandlePodEventAsync(pod, ct),
            onError: ex => _logger.LogWarning(ex, "Pod watch stream reported an error."),
            onClosed: () => _logger.LogDebug("Pod watch stream closed."));
#pragma warning restore CS0618

        // Keep the watch alive until cancellation; the Watcher runs its own read loop
        // on a background thread and invokes the callbacks above as events arrive.
        await Task.Delay(Timeout.Infinite, ct);
    }

    private async Task HandlePodEventAsync(V1Pod pod, CancellationToken ct)
    {
        try
        {
            var appLabel = pod.Metadata.Labels?.TryGetValue("app", out var lbl) == true ? lbl : null;
            if (string.IsNullOrEmpty(appLabel))
            {
                return; // not a server pod we can attribute to a deployment
            }

            var replicas = await GetDeploymentReplicasAsync(appLabel, ct);
            var status = MapStatus(replicas, pod);

            var changed = false;
            lock (_lastKnownStatus)
            {
                if (!_lastKnownStatus.TryGetValue(appLabel, out var previous) || previous != status)
                {
                    _lastKnownStatus[appLabel] = status;
                    changed = true;
                }
            }

            if (changed)
            {
                var message = new ServerStatusChangedMessage(appLabel, status, DateTimeOffset.UtcNow);
                await _hubContext.Clients.Group("events").SendAsync(HubEvents.ServerStatusChanged, message, ct);
                _logger.LogDebug("Server {Server} status changed to {Status}.", appLabel, status);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process pod event for pod {Pod}.", pod.Metadata?.Name);
        }
    }

    private async Task<int> GetDeploymentReplicasAsync(string appLabel, CancellationToken ct)
    {
        if (!_clientFactory.TryGetClient(out var client, out _))
        {
            return 0;
        }

        try
        {
            var deployment = await client!.AppsV1.ReadNamespacedDeploymentAsync(
                appLabel, _options.Namespace, cancellationToken: ct);
            return deployment.Spec?.Replicas ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Shared with KubernetesService via ServerStatusMapper so watch-driven events
    /// and REST reads agree on what "Running"/"Pending"/"Error" mean for the same pod.
    /// </summary>
    private static ServerStatus MapStatus(int replicas, V1Pod pod) =>
        ServerStatusMapper.Map(replicas, pod);
}
