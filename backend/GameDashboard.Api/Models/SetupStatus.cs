namespace GameDashboard.Api.Models;

/// <summary>
/// First-run setup/readiness snapshot (docs/docker-migration.md → What changes
/// for the API consumer). The k8s-era kubeconfig/namespace/metrics-server
/// checks collapsed into one required condition — the Docker engine being
/// reachable. Secrets are no longer part of setup: each server manages its own
/// secrets from its detail page, so there is nothing global to report here.
/// </summary>
public record SetupStatus(
    bool DockerEngineReachable,
    bool MetricsAvailable,
    IReadOnlyList<string> Warnings);
