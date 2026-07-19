using Docker.DotNet;
using GameDashboard.Api.Configuration;
using Microsoft.Extensions.Options;

namespace GameDashboard.Api.Services.Docker;

/// <summary>
/// Lazily builds the Docker Engine client, probing candidate endpoints in
/// order (docs/docker-migration.md → Engine discovery):
///   1. the configured Dashboard:DockerEndpoint override,
///   2. the platform default (npipe on Windows / unix socket on Linux — covers
///      an installed Docker Desktop or native engine),
///   3. tcp://127.0.0.1:2375 (the bundled WSL runtime's loopback-only dockerd).
///
/// Construction must never throw on a missing or unreachable engine — the
/// backend has to start up and simply report an unhealthy status. Callers call
/// <see cref="TryGetClientAsync"/> and handle a null result.
///
/// Unlike the old KubernetesClientFactory, a failed probe is retried on the
/// next call (cheap: a ping per candidate) so the dashboard recovers without a
/// restart the moment the engine comes up — the k8s factory could cache a
/// permanently-poisoned "no kubeconfig" result because config files don't
/// appear mid-session, but an engine starting up absolutely does.
///
/// The cached client is likewise re-pinged on every call: engines also *die*
/// mid-session (Docker Desktop closed, WSL VM shut down), and a stale cached
/// client would otherwise pin the dashboard to a dead endpoint until restart.
/// </summary>
public interface IDockerClientFactory
{
    /// <summary>
    /// Returns the cached reachable client, or probes candidates and caches the
    /// first that answers a ping. Null (with an error message) when no engine
    /// is reachable — never throws.
    /// </summary>
    Task<(IDockerClient? Client, string? Error)> TryGetClientAsync(CancellationToken ct);
}

public sealed class DockerClientFactory : IDockerClientFactory, IDisposable
{
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(3);

    private readonly DashboardOptions _options;
    private readonly ILogger<DockerClientFactory> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IDockerClient? _cachedClient;
    private string? _lastError;

    public DockerClientFactory(IOptions<DashboardOptions> options, ILogger<DockerClientFactory> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<(IDockerClient? Client, string? Error)> TryGetClientAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Re-verify the cached client on every call: an engine can die (or
            // be replaced — Docker Desktop closed, bundled runtime started)
            // mid-session, and a stale cached client would pin the dashboard
            // to a dead endpoint until restart. The ping is a loopback GET
            // (~1ms); on failure the cache is dropped and candidates re-probed
            // below, so the app self-heals onto whichever engine is up now.
            if (_cachedClient is not null)
            {
                if (await PingAsync(_cachedClient, "cached endpoint", new List<string>(), ct))
                {
                    return (_cachedClient, null);
                }

                _logger.LogInformation("Cached Docker engine stopped answering; re-probing endpoints.");
                _cachedClient.Dispose();
                _cachedClient = null;
            }

            var failures = new List<string>();
            foreach (var endpoint in CandidateEndpoints())
            {
                var client = TryCreate(endpoint, failures);
                if (client is null)
                {
                    continue;
                }

                if (await PingAsync(client, endpoint, failures, ct))
                {
                    _logger.LogInformation("Connected to Docker engine at {Endpoint}.", endpoint);
                    _cachedClient = client;
                    _lastError = null;
                    return (client, null);
                }

                client.Dispose();
            }

            _lastError = failures.Count > 0
                ? string.Join(" | ", failures)
                : "No Docker engine endpoint candidates.";
            return (null, _lastError);
        }
        finally
        {
            _gate.Release();
        }
    }

    private IEnumerable<string> CandidateEndpoints()
    {
        if (!string.IsNullOrWhiteSpace(_options.DockerEndpoint))
        {
            // An explicit override is trusted exclusively — mixing it with
            // fallbacks would mask a misconfiguration behind a different engine.
            yield return _options.DockerEndpoint;
            yield break;
        }

        yield return OperatingSystem.IsWindows()
            ? "npipe://./pipe/docker_engine"
            : "unix:///var/run/docker.sock";

        // The bundled WSL runtime (Windows) exposes dockerd on loopback TCP.
        if (OperatingSystem.IsWindows())
        {
            yield return "tcp://127.0.0.1:2375";
        }
    }

    private IDockerClient? TryCreate(string endpoint, List<string> failures)
    {
        try
        {
            return new DockerClientConfiguration(new Uri(endpoint)).CreateClient();
        }
        catch (Exception ex)
        {
            failures.Add($"{endpoint}: {ex.Message}");
            return null;
        }
    }

    private async Task<bool> PingAsync(
        IDockerClient client, string endpoint, List<string> failures, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PingTimeout);
            await client.System.PingAsync(cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            failures.Add($"{endpoint}: {(ex is OperationCanceledException ? "ping timed out" : ex.Message)}");
            return false;
        }
    }

    public void Dispose()
    {
        _cachedClient?.Dispose();
        _gate.Dispose();
    }
}
