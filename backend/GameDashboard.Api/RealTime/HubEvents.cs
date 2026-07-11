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
    public const string DownloadProgress = "DownloadProgress";
}

public record LogLineMessage(string ServerName, string Line, DateTimeOffset Timestamp);

public record ServerStatusChangedMessage(string ServerName, ServerStatus Status, DateTimeOffset Timestamp);

public record AutoScaleActionMessage(string ServerName, string Action, string Reason, DateTimeOffset Timestamp);

/// <summary>
/// Bytes accumulating on a server's data volume during its first deploy, measured
/// by exec-ing `du` inside the pod — a robust download indicator that works for
/// every image, unlike parsing steamcmd log output. CapacityBytes is the PVC size,
/// not the expected install size, so clients should present BytesUsed as an
/// absolute ("12.4 GiB downloaded"), not as a percentage. Pushed to the "events"
/// group by DownloadProgressService while a recently created server is starting.
/// </summary>
public record DownloadProgressMessage(
    string ServerName, long BytesUsed, long CapacityBytes, DateTimeOffset Timestamp);
