using k8s;

namespace GameDashboard.Api.Services;

/// <summary>
/// Lazily builds the KubernetesClientConfiguration / IKubernetes client.
///
/// Construction must never throw on a missing or unreachable cluster (Req 1.3) — the
/// backend has to start up and simply report an unhealthy status. Callers that need
/// the client should call <see cref="TryGetClient"/> and handle a null result rather
/// than assuming connectivity.
/// </summary>
public interface IKubernetesClientFactory
{
    /// <summary>
    /// Attempts to build (or return the cached) Kubernetes client. Returns null and
    /// captures the error if the kubeconfig is missing/invalid — never throws.
    /// </summary>
    bool TryGetClient(out IKubernetes? client, out string? error);
}

public sealed class KubernetesClientFactory : IKubernetesClientFactory
{
    private readonly ILogger<KubernetesClientFactory> _logger;
    private readonly Lock _lock = new();
    private IKubernetes? _cachedClient;
    private string? _lastError;
    private bool _attempted;

    public KubernetesClientFactory(ILogger<KubernetesClientFactory> logger)
    {
        _logger = logger;
    }

    public bool TryGetClient(out IKubernetes? client, out string? error)
    {
        lock (_lock)
        {
            if (_cachedClient is not null)
            {
                client = _cachedClient;
                error = null;
                return true;
            }

            if (_attempted)
            {
                // Previously failed; don't retry construction on every call, but do
                // allow health checks to keep reporting the cached error.
                client = null;
                error = _lastError;
                return false;
            }

            _attempted = true;

            try
            {
                var config = KubernetesClientConfiguration.IsInCluster()
                    ? KubernetesClientConfiguration.InClusterConfig()
                    : KubernetesClientConfiguration.BuildConfigFromConfigFile();

                _cachedClient = new Kubernetes(config);
                client = _cachedClient;
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Unable to build Kubernetes client. The backend will continue running " +
                    "and report cluster status as unreachable until this is resolved.");
                _lastError = ex.Message;
                client = null;
                error = _lastError;
                return false;
            }
        }
    }
}
