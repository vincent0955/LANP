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
    /// Values are never returned in bulk or logged; reading a value back requires
    /// an explicit per-key request via <see cref="GetSecretValueAsync"/>
    /// (a deliberate relaxation of Req 13.3's original write-only rule so the
    /// dashboard can offer click-to-reveal).
    /// </summary>
    Task SetSecretsAsync(IDictionary<string, string> values, CancellationToken ct);

    /// <summary>
    /// Reads a single value from the game-secrets Secret for click-to-reveal in
    /// the UI. Throws <see cref="KeyNotFoundException"/> (→ 404) when the Secret
    /// or the key does not exist.
    /// </summary>
    Task<string> GetSecretValueAsync(string key, CancellationToken ct);

    /// <summary>
    /// Removes a single key from the game-secrets Secret. Throws
    /// <see cref="KeyNotFoundException"/> (→ 404) when the Secret or the key does
    /// not exist.
    /// </summary>
    Task DeleteSecretKeyAsync(string key, CancellationToken ct);
}
