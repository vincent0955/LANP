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
    /// Full first-run setup/readiness snapshot: engine reachability and metrics
    /// availability. Never throws.
    /// </summary>
    Task<SetupStatus> GetSetupStatusAsync(CancellationToken ct);

    /// <summary>
    /// Lists the secrets a server's game template requires (the store keys from
    /// its secretKeyRefs) and whether each currently has a value set for this
    /// server. Never returns the values themselves. Throws
    /// <see cref="Exceptions.ServerNotFoundException"/> (→ 404) when the server
    /// does not exist.
    /// </summary>
    Task<IReadOnlyList<ServerSecretInfo>> GetServerSecretsAsync(string name, CancellationToken ct);

    /// <summary>
    /// Sets one or more of a server's secrets in the local encrypted store and
    /// recreates the container so the new values take effect. Only keys the
    /// server's template references are accepted (others → 400). Values are
    /// never returned in bulk or logged.
    /// </summary>
    Task SetServerSecretsAsync(string name, IDictionary<string, string> values, CancellationToken ct);

    /// <summary>
    /// Reads a single secret value for click-to-reveal in the UI. Throws
    /// <see cref="KeyNotFoundException"/> (→ 404) when no value is set for that
    /// key on this server.
    /// </summary>
    Task<string> GetServerSecretValueAsync(string name, string key, CancellationToken ct);

    /// <summary>
    /// Clears a single secret for a server and recreates the container so the
    /// value stops being injected. Throws <see cref="KeyNotFoundException"/>
    /// (→ 404) when no value is set for that key on this server.
    /// </summary>
    Task DeleteServerSecretAsync(string name, string key, CancellationToken ct);

    /// <summary>
    /// Replaces an app-managed secret (e.g. an RCON password) with a freshly
    /// generated strong value and recreates the container so it takes effect.
    /// Throws <see cref="ArgumentException"/> (→ 400) when the key is not an
    /// app-managed secret on this server (user-supplied secrets have no value the
    /// app can mint; set those directly instead).
    /// </summary>
    Task RegenerateServerSecretAsync(string name, string key, CancellationToken ct);
}
