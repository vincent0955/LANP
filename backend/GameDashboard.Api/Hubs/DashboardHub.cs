using GameDashboard.Api.RealTime;
using Microsoft.AspNetCore.SignalR;

namespace GameDashboard.Api.Hubs;

/// <summary>
/// Real-time hub for the dashboard: log streaming, server status events, and metrics
/// push. See design.md → SignalR Hub (/hubs/dashboard); requirements.md → Req 7, 8, 9.
///
/// Group membership model:
///   "logs:{serverName}" — subscribers of a specific server's live logs
///   "events"            — subscribers of ServerStatusChanged / AutoScaleAction
///   "metrics"           — subscribers of periodic MetricsUpdate pushes
///
/// The hub itself only manages group membership and notifies
/// <see cref="ILogStreamManager"/> of subscriber count changes so the underlying
/// Kubernetes log stream can be started/stopped accordingly (Req 7.5). Status and
/// metrics pushes are driven by background services (PodWatchService, Phase 6
/// MetricsService) writing directly to <see cref="IHubContext{DashboardHub}"/>.
/// </summary>
public sealed class DashboardHub : Hub
{
    private const string EventsGroup = "events";
    private const string MetricsGroup = "metrics";

    private readonly ILogStreamManager _logStreamManager;
    private readonly ILogger<DashboardHub> _logger;

    public DashboardHub(ILogStreamManager logStreamManager, ILogger<DashboardHub> logger)
    {
        _logStreamManager = logStreamManager;
        _logger = logger;
    }

    public async Task SubscribeLogs(string serverName)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, LogsGroupFor(serverName));
        await _logStreamManager.SubscribeAsync(serverName, Context.ConnectionId, Context.ConnectionAborted);
    }

    public async Task UnsubscribeLogs(string serverName)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, LogsGroupFor(serverName));
        await _logStreamManager.UnsubscribeAsync(serverName, Context.ConnectionId);
    }

    public Task SubscribeEvents() => Groups.AddToGroupAsync(Context.ConnectionId, EventsGroup);

    public Task SubscribeMetrics() => Groups.AddToGroupAsync(Context.ConnectionId, MetricsGroup);

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // SignalR removes the connection from all groups automatically on disconnect,
        // but the LogStreamManager tracks subscriber counts independently of group
        // membership, so it must be told explicitly which log streams this connection
        // was following.
        foreach (var serverName in _logStreamManager.ServerNamesSubscribedBy(Context.ConnectionId).ToList())
        {
            await _logStreamManager.UnsubscribeAsync(serverName, Context.ConnectionId);
        }

        _logger.LogDebug("Connection {ConnectionId} disconnected from dashboard hub.", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public static string LogsGroupFor(string serverName) => $"logs:{serverName}";
}
