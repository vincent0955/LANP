namespace GameDashboard.Api.Models;

/// <summary>
/// First-run setup/readiness snapshot (docs/docker-migration.md → What changes
/// for the API consumer). The k8s-era kubeconfig/namespace/metrics-server
/// checks collapsed into one required condition — the Docker engine being
/// reachable — plus optional secrets, which are reported as warnings rather
/// than failures so the dashboard can still function with reduced capability.
/// </summary>
public record SetupStatus(
    bool DockerEngineReachable,
    bool MetricsAvailable,
    bool SecretsConfigured,
    IReadOnlyList<string> ConfiguredSecretKeys,
    IReadOnlyList<string> Warnings);
