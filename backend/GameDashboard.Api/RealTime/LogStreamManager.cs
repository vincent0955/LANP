using System.Collections.Concurrent;
using GameDashboard.Api.Configuration;
using GameDashboard.Api.Hubs;
using GameDashboard.Api.Services;
using k8s;
using k8s.Autorest;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace GameDashboard.Api.RealTime;

/// <summary>
/// See <see cref="ILogStreamManager"/>. One background read loop per server with at
/// least one subscriber; fans lines out to all SignalR connections in that server's
/// log group. Torn down when the last subscriber unsubscribes or disconnects.
/// </summary>
public sealed class LogStreamManager : ILogStreamManager, IDisposable
{
    private const int ReplayLineCount = 200;

    private readonly IKubernetesClientFactory _clientFactory;
    private readonly IHubContext<DashboardHub> _hubContext;
    private readonly DashboardOptions _options;
    private readonly ILogger<LogStreamManager> _logger;

    private readonly object _lock = new();
    private readonly Dictionary<string, StreamState> _streamsByServer = new();

    public LogStreamManager(
        IKubernetesClientFactory clientFactory,
        IHubContext<DashboardHub> hubContext,
        IOptions<DashboardOptions> options,
        ILogger<LogStreamManager> logger)
    {
        _clientFactory = clientFactory;
        _hubContext = hubContext;
        _options = options.Value;
        _logger = logger;
    }

    public async Task SubscribeAsync(string serverName, string connectionId, CancellationToken ct)
    {
        StreamState state;
        bool isNewStream;

        lock (_lock)
        {
            if (!_streamsByServer.TryGetValue(serverName, out state!))
            {
                state = new StreamState();
                _streamsByServer[serverName] = state;
                isNewStream = true;
            }
            else
            {
                isNewStream = false;
            }

            state.SubscriberConnectionIds.Add(connectionId);
        }

        if (isNewStream)
        {
            state.Cts = new CancellationTokenSource();
            state.ReadLoopTask = Task.Run(() => RunReadLoopAsync(serverName, state, state.Cts.Token));
        }

        // Replay recent history to the newly subscribed connection only (Req 7.3).
        var recentLines = state.RecentLines.ToArray();
        var client = _hubContext.Clients.Client(connectionId);
        foreach (var line in recentLines)
        {
            await client.SendAsync(HubEvents.LogLine, new LogLineMessage(serverName, line, DateTimeOffset.UtcNow), ct);
        }
    }

    public async Task UnsubscribeAsync(string serverName, string connectionId)
    {
        CancellationTokenSource? ctsToCancel = null;

        lock (_lock)
        {
            if (!_streamsByServer.TryGetValue(serverName, out var state))
            {
                return;
            }

            state.SubscriberConnectionIds.Remove(connectionId);

            if (state.SubscriberConnectionIds.Count == 0)
            {
                _streamsByServer.Remove(serverName);
                ctsToCancel = state.Cts;
            }
        }

        if (ctsToCancel is not null)
        {
            await ctsToCancel.CancelAsync();
            ctsToCancel.Dispose();
            _logger.LogDebug("Stopped log stream for {ServerName}: no subscribers remain.", serverName);
        }
    }

    public IReadOnlyCollection<string> ServerNamesSubscribedBy(string connectionId)
    {
        lock (_lock)
        {
            return _streamsByServer
                .Where(kv => kv.Value.SubscriberConnectionIds.Contains(connectionId))
                .Select(kv => kv.Key)
                .ToList();
        }
    }

    public int SubscriberCount(string serverName)
    {
        lock (_lock)
        {
            return _streamsByServer.TryGetValue(serverName, out var state)
                ? state.SubscriberConnectionIds.Count
                : 0;
        }
    }

    private async Task RunReadLoopAsync(string serverName, StreamState state, CancellationToken ct)
    {
        if (!_clientFactory.TryGetClient(out var client, out var error))
        {
            _logger.LogWarning(
                "Cannot start log stream for {ServerName}: cluster unreachable ({Error}).", serverName, error);
            return;
        }

        try
        {
            using var stream = await client!.CoreV1.ReadNamespacedPodLogAsync(
                name: await ResolvePodNameAsync(client, serverName, ct),
                namespaceParameter: _options.Namespace,
                follow: true,
                tailLines: ReplayLineCount,
                cancellationToken: ct);

            using var reader = new StreamReader(stream);
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                {
                    break; // stream ended (pod stopped / log closed)
                }

                state.RecentLines.Enqueue(line);
                while (state.RecentLines.Count > ReplayLineCount)
                {
                    state.RecentLines.TryDequeue(out _);
                }

                var group = _hubContext.Clients.Group(DashboardHub.LogsGroupFor(serverName));
                await group.SendAsync(
                    HubEvents.LogLine, new LogLineMessage(serverName, line, DateTimeOffset.UtcNow), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on unsubscribe/shutdown.
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogInformation("Log stream for {ServerName} ended: pod not found.", serverName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Log stream for {ServerName} terminated unexpectedly.", serverName);
        }
    }

    private async Task<string> ResolvePodNameAsync(IKubernetes client, string serverName, CancellationToken ct)
    {
        var deployment = await client.AppsV1.ReadNamespacedDeploymentAsync(serverName, _options.Namespace, cancellationToken: ct);
        var appLabel = deployment.Spec?.Selector?.MatchLabels?.TryGetValue("app", out var lbl) == true ? lbl : serverName;

        var pods = await client.CoreV1.ListNamespacedPodAsync(
            _options.Namespace, labelSelector: $"app={appLabel}", cancellationToken: ct);

        var pod = pods.Items.FirstOrDefault()
            ?? throw new InvalidOperationException($"No pod found for server '{serverName}'.");

        return pod.Metadata.Name;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var state in _streamsByServer.Values)
            {
                state.Cts?.Cancel();
                state.Cts?.Dispose();
            }
            _streamsByServer.Clear();
        }
    }

    private sealed class StreamState
    {
        public HashSet<string> SubscriberConnectionIds { get; } = new();
        public ConcurrentQueue<string> RecentLines { get; } = new();
        public CancellationTokenSource? Cts { get; set; }
        public Task? ReadLoopTask { get; set; }
    }
}
