namespace GameDashboard.Api.RealTime;

/// <summary>
/// Manages live log streaming fan-out for the dashboard hub. One underlying
/// Kubernetes log stream is kept per server, shared across all subscribing
/// connections, and torn down once the last subscriber leaves.
/// See requirements.md → Req 7.
/// </summary>
public interface ILogStreamManager
{
    /// <summary>
    /// Registers <paramref name="connectionId"/> as a subscriber of
    /// <paramref name="serverName"/>'s logs. Starts the underlying Kubernetes log
    /// stream if this is the first subscriber for that server, and replays the last
    /// N buffered lines to the caller immediately (Req 7.3).
    /// </summary>
    Task SubscribeAsync(string serverName, string connectionId, CancellationToken ct);

    /// <summary>
    /// Removes <paramref name="connectionId"/>'s subscription to
    /// <paramref name="serverName"/>. Stops the underlying stream once no
    /// subscribers remain for that server (Req 7.5).
    /// </summary>
    Task UnsubscribeAsync(string serverName, string connectionId);

    /// <summary>
    /// The set of server names <paramref name="connectionId"/> is currently
    /// subscribed to. Used by <see cref="Hubs.DashboardHub.OnDisconnectedAsync"/> to
    /// clean up subscriptions on disconnect.
    /// </summary>
    IReadOnlyCollection<string> ServerNamesSubscribedBy(string connectionId);

    /// <summary>Current subscriber count for a server's log stream (for diagnostics/tests).</summary>
    int SubscriberCount(string serverName);
}
