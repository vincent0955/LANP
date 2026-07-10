namespace GameDashboard.Api.Models;

/// <summary>
/// Reachability/readiness snapshot of the Kubernetes cluster.
/// See requirements.md → Req 1, Req 13.
/// </summary>
public record ClusterHealth(
    bool ClusterReachable,
    bool NamespaceReady,
    string Namespace,
    string? Error);
