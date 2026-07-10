using GameDashboard.Api.Models;

namespace GameDashboard.Api.Services;

/// <summary>
/// Owns all direct interaction with the Kubernetes API for game server management.
/// See design.md → Components and Interfaces → KubernetesService.
///
/// Phase 2 implements the read-only surface (health, namespace bootstrap, list/get).
/// The write operations (deploy/scale/delete/config) are introduced in Phases 3-4 and
/// throw NotImplementedException until then, so the interface can be depended upon
/// by controllers immediately.
/// </summary>
public interface IKubernetesService
{
    Task<IReadOnlyList<ServerSummary>> ListServersAsync(CancellationToken ct);

    Task<ServerDetail?> GetServerAsync(string name, CancellationToken ct);

    Task<ServerDetail> DeployServerAsync(DeployServerRequest request, CancellationToken ct);

    Task ScaleServerAsync(string name, int replicas, CancellationToken ct);

    Task DeleteServerAsync(string name, bool deleteData, CancellationToken ct);

    Task<IDictionary<string, string>> GetConfigAsync(string name, CancellationToken ct);

    Task UpdateConfigAsync(string name, IDictionary<string, string> values, CancellationToken ct);

    Task EnsureNamespaceAsync(CancellationToken ct);

    Task<ClusterHealth> GetHealthAsync(CancellationToken ct);

    /// <summary>
    /// Reads the last-active timestamp for a server, stored as a Deployment
    /// annotation. Returns the Deployment's creation time if no annotation is set
    /// yet (a server that has never been evaluated is treated as active as of its
    /// creation, not as infinitely stale). See requirements.md → Req 12 (Task 8.1).
    /// </summary>
    Task<DateTimeOffset> GetLastActiveAsync(string name, CancellationToken ct);

    /// <summary>
    /// Updates the last-active annotation for a server to the given timestamp.
    /// Called by AutoScaleService on each evaluation for servers with active players.
    /// </summary>
    Task SetLastActiveAsync(string name, DateTimeOffset timestamp, CancellationToken ct);

    /// <summary>
    /// Full first-run setup/readiness snapshot (Req 13.2): kubeconfig presence,
    /// cluster reachability, namespace readiness, metrics-server presence, and
    /// whether the game-secrets Secret exists. Never throws.
    /// </summary>
    Task<SetupStatus> GetSetupStatusAsync(CancellationToken ct);

    /// <summary>
    /// Creates or updates the game-secrets Secret with the given key/value pairs.
    /// Write-only: there is no corresponding read method, by design (Req 13.3,
    /// Req 14.3) — secret values must never be readable back through this API.
    /// </summary>
    Task SetSecretsAsync(IDictionary<string, string> values, CancellationToken ct);
}
