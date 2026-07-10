using GameDashboard.Api.Models;

namespace GameDashboard.Api.RealTime;

/// <summary>
/// Server-to-client message payloads pushed over the DashboardHub. Kept as plain
/// records (rather than positional Hub method calls scattered across the codebase)
/// so the wire contract is defined in exactly one place.
/// See design.md → SignalR Hub (/hubs/dashboard).
/// </summary>
public static class HubEvents
{
    public const string LogLine = "LogLine";
    public const string ServerStatusChanged = "ServerStatusChanged";
    public const string MetricsUpdate = "MetricsUpdate";
    public const string AutoScaleAction = "AutoScaleAction";
}

public record LogLineMessage(string ServerName, string Line, DateTimeOffset Timestamp);

public record ServerStatusChangedMessage(string ServerName, ServerStatus Status, DateTimeOffset Timestamp);

public record AutoScaleActionMessage(string ServerName, string Action, string Reason, DateTimeOffset Timestamp);
