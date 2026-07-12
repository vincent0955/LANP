namespace GameDashboard.Api.Models;

/// <summary>
/// First-run setup/readiness snapshot. See requirements.md → Req 13.2.
///
/// Distinguishes required conditions (kubeconfig, cluster, namespace) from optional
/// ones (metrics-server, secrets) — missing optional components are reported as
/// warnings, not failures (Req 13.4), so the dashboard can still function with
/// reduced capability rather than refusing to start.
/// </summary>
public record SetupStatus(
    bool KubeconfigPresent,
    bool ClusterReachable,
    bool NamespaceReady,
    bool MetricsServerPresent,
    bool SecretsConfigured,
    IReadOnlyList<string> ConfiguredSecretKeys,
    IReadOnlyList<string> Warnings);
