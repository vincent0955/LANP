namespace GameDashboard.Api.Models;

/// <summary>
/// Node-level resource usage/capacity snapshot. See design.md → NodeMetrics.
/// Capacity fields come from the Node object itself (always available); usage
/// fields come from the metrics-server API (only available if installed).
/// </summary>
public record NodeMetrics(
    double CpuUsedMillicores,
    double CpuCapacityMillicores,
    long MemUsedBytes,
    long MemCapacityBytes);

/// <summary>
/// Per-server (per-pod) resource usage snapshot.
/// See requirements.md → Req 9.2.
/// </summary>
public record PodMetricsInfo(
    string ServerName,
    double CpuUsedMillicores,
    long MemUsedBytes);

/// <summary>
/// Top-level metrics response. When the metrics-server is not installed in the
/// cluster, <see cref="Available"/> is false and both collections are empty rather
/// than the endpoint failing (Req 9.3).
/// </summary>
public record MetricsSnapshot(
    bool Available,
    NodeMetrics? Node,
    IReadOnlyList<PodMetricsInfo> Pods,
    string? UnavailableReason);
