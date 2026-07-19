using GameDashboard.Api.Models;

namespace GameDashboard.Api.Services;

/// <summary>
/// Owns all direct interaction with the container engine for game server
/// management. Formerly IKubernetesService (see docs/docker-migration.md);
/// the REST/SignalR surface built on top of it is unchanged, servers are just
/// plain Docker containers now instead of Kubernetes Deployments.
/// </summary>
public interface IServerOrchestrator
{
    Task<IReadOnlyList<ServerSummary>> ListServersAsync(CancellationToken ct);

    Task<ServerDetail?> GetServerAsync(string name, CancellationToken ct);

    /// <summary>
    /// Validates the request, allocates ports, and registers the server; the
    /// image pull + container create/start run in the background because
    /// first-time pulls take minutes (docs/docker-migration.md → Deploy is
    /// asynchronous). The returned detail is the Pending placeholder.
    /// </summary>
    Task<ServerDetail> DeployServerAsync(DeployServerRequest request, CancellationToken ct);

    /// <summary>Replicas semantics kept from the k8s era: 1 = start, 0 = stop.</summary>
    Task ScaleServerAsync(string name, int replicas, CancellationToken ct);

    Task DeleteServerAsync(string name, bool deleteData, CancellationToken ct);

    Task<IDictionary<string, string>> GetConfigAsync(string name, CancellationToken ct);

    Task UpdateConfigAsync(string name, IDictionary<string, string> values, CancellationToken ct);

    Task<ClusterHealth> GetHealthAsync(CancellationToken ct);

    /// <summary>
    /// Reads the last-active timestamp for a server (LastActiveStore file).
    /// Falls back to the server's creation time when nothing was recorded yet
    /// (a server that has never been evaluated is treated as active as of its
    /// creation, not as infinitely stale).
    /// </summary>
    Task<DateTimeOffset> GetLastActiveAsync(string name, CancellationToken ct);

    /// <summary>
    /// Updates the last-active timestamp for a server. Called by
    /// AutoScaleService on each evaluation for servers with active players.
    /// </summary>
    Task SetLastActiveAsync(string name, DateTimeOffset timestamp, CancellationToken ct);

    /// <summary>
    /// Full first-run setup/readiness snapshot: engine reachability, metrics
    /// availability, and configured secrets. Never throws.
    /// </summary>
    Task<SetupStatus> GetSetupStatusAsync(CancellationToken ct);

    /// <summary>
    /// Creates or updates secrets in the local encrypted store. Values are
    /// never returned in bulk or logged; reading a value back requires an
    /// explicit per-key request via <see cref="GetSecretValueAsync"/>.
    /// </summary>
    Task SetSecretsAsync(IDictionary<string, string> values, CancellationToken ct);

    /// <summary>
    /// Reads a single secret value for click-to-reveal in the UI. Throws
    /// <see cref="KeyNotFoundException"/> (→ 404) when the key does not exist.
    /// </summary>
    Task<string> GetSecretValueAsync(string key, CancellationToken ct);

    /// <summary>
    /// Removes a single secret key. Throws <see cref="KeyNotFoundException"/>
    /// (→ 404) when the key does not exist; keys referenced by a curated game
    /// template are rejected with <see cref="InvalidOperationException"/> (→ 409).
    /// </summary>
    Task DeleteSecretKeyAsync(string key, CancellationToken ct);
}
