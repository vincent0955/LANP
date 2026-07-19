using System.Collections.Concurrent;
using Docker.DotNet;
using Docker.DotNet.Models;
using GameDashboard.Api.Hubs;
using GameDashboard.Api.Services.Docker;
using Microsoft.AspNetCore.SignalR;

namespace GameDashboard.Api.RealTime;

/// <summary>
/// See <see cref="ILogStreamManager"/>. One background read loop per server
/// with at least one subscriber, consuming `docker logs --follow` (multiplexed
/// stdout/stderr demuxed to lines); fans lines out to all SignalR connections
/// in that server's log group. Torn down when the last subscriber unsubscribes
/// or disconnects.
/// </summary>
public sealed class LogStreamManager : ILogStreamManager, IDisposable
{
    private const int ReplayLineCount = 200;

    private readonly IDockerClientFactory _clientFactory;
    private readonly IHubContext<DashboardHub> _hubContext;
    private readonly ILogger<LogStreamManager> _logger;

    private readonly object _lock = new();
    private readonly Dictionary<string, StreamState> _streamsByServer = new();

    public LogStreamManager(
        IDockerClientFactory clientFactory,
        IHubContext<DashboardHub> hubContext,
        ILogger<LogStreamManager> logger)
    {
        _clientFactory = clientFactory;
        _hubContext = hubContext;
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

        // Replay recent history to the newly subscribed connection only.
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
        var (client, error) = await _clientFactory.TryGetClientAsync(ct);
        if (client is null)
        {
            _logger.LogWarning(
                "Cannot start log stream for {ServerName}: Docker engine unreachable ({Error}).", serverName, error);
            return;
        }

        try
        {
            // The container is named exactly after the server; tty is always
            // false for dashboard-created containers, so the stream is
            // multiplexed stdout/stderr.
            using var stream = await client.Containers.GetContainerLogsAsync(
                serverName,
                tty: false,
                new ContainerLogsParameters
                {
                    ShowStdout = true,
                    ShowStderr = true,
                    Follow = true,
                    Tail = ReplayLineCount.ToString(),
                },
                ct);

            await foreach (var line in DockerStreamText.ReadLinesAsync(stream, ct))
            {
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
        catch (DockerContainerNotFoundException)
        {
            _logger.LogInformation("Log stream for {ServerName} ended: container not found.", serverName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Log stream for {ServerName} terminated unexpectedly.", serverName);
        }
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
