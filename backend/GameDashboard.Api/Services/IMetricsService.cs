using GameDashboard.Api.Models;

namespace GameDashboard.Api.Services;

/// <summary>
/// Reports node and per-server resource usage sourced from the Kubernetes
/// metrics-server API. See design.md → MetricsService; requirements.md → Req 9.
///
/// The metrics-server is an optional cluster add-on (not installed by default on
/// Docker Desktop). Every method on this service must degrade gracefully — never
/// throw — when it is absent; callers check <see cref="MetricsAvailable"/> or the
/// <see cref="MetricsSnapshot.Available"/> flag instead of catching exceptions.
/// </summary>
public interface IMetricsService
{
    /// <summary>
    /// True if the metrics-server API responded successfully on the most recent
    /// query. Starts as false (unknown) until the first query completes.
    /// </summary>
    bool MetricsAvailable { get; }

    /// <summary>
    /// Fetches node and per-server metrics in one call. Never throws: if the
    /// metrics-server is unavailable, returns a snapshot with
    /// <see cref="MetricsSnapshot.Available"/> = false and an explanatory reason.
    /// </summary>
    Task<MetricsSnapshot> GetSnapshotAsync(CancellationToken ct);
}
