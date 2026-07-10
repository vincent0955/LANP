using GameDashboard.Api.Configuration;
using GameDashboard.Api.Exceptions;
using GameDashboard.Api.GameTemplates;
using GameDashboard.Api.Logging;
using GameDashboard.Api.Models;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Options;

namespace GameDashboard.Api.Services;

/// <summary>
/// See <see cref="IKubernetesService"/>. Phase 2 implements health, namespace bootstrap,
/// and read (list/get) operations. Write operations are stubbed for later phases.
/// </summary>
public sealed class KubernetesService : IKubernetesService
{
    private const string AppLabel = "app";
    private const string ManagedByLabel = "app.kubernetes.io/managed-by";
    private const string ManagedByValue = "game-dashboard";

    private readonly IKubernetesClientFactory _clientFactory;
    private readonly IDeploymentBuilder _deploymentBuilder;
    private readonly IRconService _rconService;
    private readonly IMetricsService _metricsService;
    private readonly DashboardOptions _options;
    private readonly ILogger<KubernetesService> _logger;

    public KubernetesService(
        IKubernetesClientFactory clientFactory,
        IDeploymentBuilder deploymentBuilder,
        IRconService rconService,
        IMetricsService metricsService,
        IOptions<DashboardOptions> options,
        ILogger<KubernetesService> logger)
    {
        _clientFactory = clientFactory;
        _deploymentBuilder = deploymentBuilder;
        _rconService = rconService;
        _metricsService = metricsService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ClusterHealth> GetHealthAsync(CancellationToken ct)
    {
        if (!_clientFactory.TryGetClient(out var client, out var error))
        {
            return new ClusterHealth(
                ClusterReachable: false,
                NamespaceReady: false,
                Namespace: _options.Namespace,
                Error: error ?? "Kubernetes client could not be initialized.");
        }

        try
        {
            // A lightweight call that proves the API server is actually reachable,
            // not just that the client object constructed successfully.
            var namespaces = await client!.CoreV1.ListNamespaceAsync(cancellationToken: ct);
            var namespaceReady = namespaces.Items.Any(n => n.Metadata.Name == _options.Namespace);

            return new ClusterHealth(
                ClusterReachable: true,
                NamespaceReady: namespaceReady,
                Namespace: _options.Namespace,
                Error: null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cluster reachability check failed.");
            return new ClusterHealth(
                ClusterReachable: false,
                NamespaceReady: false,
                Namespace: _options.Namespace,
                Error: ex.Message);
        }
    }

    public async Task EnsureNamespaceAsync(CancellationToken ct)
    {
        if (!_clientFactory.TryGetClient(out var client, out var error))
        {
            throw new ClusterUnreachableException($"Cannot ensure namespace: {error}");
        }

        try
        {
            await client!.CoreV1.ReadNamespaceAsync(_options.Namespace, cancellationToken: ct);
            // Already exists.
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            var ns = new V1Namespace
            {
                Metadata = new V1ObjectMeta
                {
                    Name = _options.Namespace,
                    Labels = new Dictionary<string, string>
                    {
                        ["project"] = "multi-game-server-cluster"
                    }
                }
            };

            await client!.CoreV1.CreateNamespaceAsync(ns, cancellationToken: ct);
            _logger.LogInformation("Created namespace {Namespace}.", _options.Namespace);
        }
    }

    private const string LastActiveAnnotation = "game-dashboard.io/last-active-at";

    public async Task<DateTimeOffset> GetLastActiveAsync(string name, CancellationToken ct)
    {
        if (!_clientFactory.TryGetClient(out var client, out var error))
        {
            throw new ClusterUnreachableException($"Cluster unreachable: {error}");
        }

        return await RunAsync(async () =>
        {
            V1Deployment deployment;
            try
            {
                deployment = await client!.AppsV1.ReadNamespacedDeploymentAsync(
                    name, _options.Namespace, cancellationToken: ct);
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new ServerNotFoundException(name);
            }

            var annotations = deployment.Metadata.Annotations;
            if (annotations != null &&
                annotations.TryGetValue(LastActiveAnnotation, out var raw) &&
                DateTimeOffset.TryParse(raw, out var parsed))
            {
                return parsed;
            }

            // No annotation yet: treat the server as active as of its creation
            // rather than infinitely stale, so a freshly-deployed server isn't
            // immediately the top scale-down candidate (Task 8.1).
            return deployment.Metadata.CreationTimestamp is { } created
                ? new DateTimeOffset(created)
                : DateTimeOffset.UtcNow;
        });
    }

    public async Task SetLastActiveAsync(string name, DateTimeOffset timestamp, CancellationToken ct)
    {
        if (!_clientFactory.TryGetClient(out var client, out var error))
        {
            throw new ClusterUnreachableException($"Cluster unreachable: {error}");
        }

        await RunAsync(async () =>
        {
            var patch = new V1Patch(
                $"{{\"metadata\":{{\"annotations\":{{\"{LastActiveAnnotation}\":\"{timestamp:o}\"}}}}}}",
                V1Patch.PatchType.MergePatch);

            try
            {
                await client!.AppsV1.PatchNamespacedDeploymentAsync(
                    patch, name, _options.Namespace, cancellationToken: ct);
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new ServerNotFoundException(name);
            }

            return true;
        });
    }

    public async Task<SetupStatus> GetSetupStatusAsync(CancellationToken ct)
    {
        // Kubeconfig presence and cluster reachability share the same underlying
        // check: if the client factory can produce a client and a live call
        // succeeds, both are true. This mirrors GetHealthAsync's approach.
        var kubeconfigPresent = _clientFactory.TryGetClient(out var client, out _);
        var warnings = new List<string>();

        if (!kubeconfigPresent)
        {
            warnings.Add("Kubernetes configuration could not be loaded. Is Docker Desktop running?");
            return new SetupStatus(
                KubeconfigPresent: false,
                ClusterReachable: false,
                NamespaceReady: false,
                MetricsServerPresent: false,
                SecretsConfigured: false,
                Warnings: warnings);
        }

        bool clusterReachable;
        bool namespaceReady;
        try
        {
            var namespaces = await client!.CoreV1.ListNamespaceAsync(cancellationToken: ct);
            clusterReachable = true;
            namespaceReady = namespaces.Items.Any(n => n.Metadata.Name == _options.Namespace);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Setup status: cluster reachability check failed.");
            clusterReachable = false;
            namespaceReady = false;
        }

        if (!clusterReachable)
        {
            warnings.Add("Kubernetes cluster is unreachable. Is Docker Desktop running and Kubernetes enabled?");
        }
        else if (!namespaceReady)
        {
            warnings.Add($"Namespace '{_options.Namespace}' does not exist yet; it will be created automatically on next use.");
        }

        // metrics-server (Req 13.4: optional component, warning only).
        var metricsSnapshot = await _metricsService.GetSnapshotAsync(ct);
        if (!metricsSnapshot.Available)
        {
            warnings.Add(
                "metrics-server is not installed. Resource metrics and automatic scaling will be unavailable " +
                "until it is installed in the cluster.");
        }

        // game-secrets Secret (Req 13.4: optional at startup, but needed for CS2
        // public listing / RCON — existence check only, never read the contents).
        var secretsConfigured = false;
        if (clusterReachable)
        {
            try
            {
                await client!.CoreV1.ReadNamespacedSecretAsync(
                    _options.SecretName, _options.Namespace, cancellationToken: ct);
                secretsConfigured = true;
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                warnings.Add(
                    $"Secret '{_options.SecretName}' does not exist yet. Use POST /api/setup/secrets " +
                    "to configure RCON passwords and the CS2 Steam Game Server Login Token.");
            }
        }

        return new SetupStatus(
            KubeconfigPresent: true,
            ClusterReachable: clusterReachable,
            NamespaceReady: namespaceReady,
            MetricsServerPresent: metricsSnapshot.Available,
            SecretsConfigured: secretsConfigured,
            Warnings: warnings);
    }

    public async Task SetSecretsAsync(IDictionary<string, string> values, CancellationToken ct)
    {
        if (!_clientFactory.TryGetClient(out var client, out var error))
        {
            throw new ClusterUnreachableException($"Cluster unreachable: {error}");
        }

        await RunAsync(async () =>
        {
            var secretData = values.ToDictionary(
                kv => kv.Key,
                kv => System.Text.Encoding.UTF8.GetBytes(kv.Value));

            try
            {
                var existing = await client!.CoreV1.ReadNamespacedSecretAsync(
                    _options.SecretName, _options.Namespace, cancellationToken: ct);

                // Merge rather than replace outright, so callers can update a
                // subset of keys (e.g. just the CS2 token) without clobbering
                // other secret values already configured.
                existing.Data ??= new Dictionary<string, byte[]>();
                foreach (var (key, bytes) in secretData)
                {
                    existing.Data[key] = bytes;
                }

                await client.CoreV1.ReplaceNamespacedSecretAsync(
                    existing, _options.SecretName, _options.Namespace, cancellationToken: ct);
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                var secret = new V1Secret
                {
                    ApiVersion = "v1",
                    Kind = "Secret",
                    Type = "Opaque",
                    Metadata = new V1ObjectMeta
                    {
                        Name = _options.SecretName,
                        NamespaceProperty = _options.Namespace
                    },
                    Data = secretData
                };

                await client!.CoreV1.CreateNamespacedSecretAsync(secret, _options.Namespace, cancellationToken: ct);
            }

            // Log only the key names, never the values (Req 13.3, Req 14.3).
            _logger.LogInformation(
                "Updated secret {SecretName} with keys: {Keys}",
                _options.SecretName, string.Join(", ", values.Keys));

            return true;
        });
    }

    public async Task<IReadOnlyList<ServerSummary>> ListServersAsync(CancellationToken ct)
    {
        if (!_clientFactory.TryGetClient(out var client, out var error))
        {
            throw new ClusterUnreachableException($"Cluster unreachable: {error}");
        }

        return await RunAsync(async () =>
        {
            var deployments = await client!.AppsV1.ListNamespacedDeploymentAsync(
                _options.Namespace, cancellationToken: ct);

            if (deployments.Items.Count == 0)
            {
                return (IReadOnlyList<ServerSummary>)Array.Empty<ServerSummary>();
            }

            var pods = await client.CoreV1.ListNamespacedPodAsync(_options.Namespace, cancellationToken: ct);

            var summaries = new List<ServerSummary>(deployments.Items.Count);
            foreach (var deployment in deployments.Items)
            {
                var matchingPods = FindPodsForDeployment(deployment, pods.Items);
                summaries.Add(MapToSummary(deployment, matchingPods));
            }

            return summaries;
        });
    }

    public async Task<ServerDetail?> GetServerAsync(string name, CancellationToken ct)
    {
        if (!_clientFactory.TryGetClient(out var client, out var error))
        {
            throw new ClusterUnreachableException($"Cluster unreachable: {error}");
        }

        return await RunAsync(async () =>
        {
            V1Deployment deployment;
            try
            {
                deployment = await client!.AppsV1.ReadNamespacedDeploymentAsync(
                    name, _options.Namespace, cancellationToken: ct);
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            var pods = await client!.CoreV1.ListNamespacedPodAsync(_options.Namespace, cancellationToken: ct);
            var matchingPods = FindPodsForDeployment(deployment, pods.Items);

            var services = await client.CoreV1.ListNamespacedServiceAsync(_options.Namespace, cancellationToken: ct);
            var service = FindServiceForDeployment(deployment, services.Items);

            var detail = MapToDetail(deployment, matchingPods, service);

            // Only worth querying RCON for a server that is actually up (Req 10.2);
            // querying a stopped server would just wait out a connect timeout.
            if (detail.Status == ServerStatus.Running)
            {
                var players = await _rconService.QueryPlayerInfoAsync(name, ct);
                detail = detail with { Players = players };
            }

            return (ServerDetail?)detail;
        });
    }

    /// <summary>
    /// Runs a Kubernetes API call and translates low-level connectivity failures
    /// (socket errors, DNS failures, TLS handshake failures against an unreachable
    /// API server) into <see cref="ClusterUnreachableException"/> so callers/middleware
    /// report 503 rather than a generic 500. HttpOperationException (a real HTTP
    /// response from a reachable API server, e.g. 404/409) is left to propagate as-is.
    /// </summary>
    private static async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (HttpOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException
            or System.Net.WebException or TaskCanceledException)
        {
            throw new ClusterUnreachableException($"Cluster unreachable: {ex.Message}");
        }
    }

    public async Task<ServerDetail> DeployServerAsync(DeployServerRequest request, CancellationToken ct)
    {
        ServerNameValidator.EnsureValid(request.Name);

        // Deploy is currently limited to curated games; Phase 7 extends this to the
        // full LinuxGSM catalog.
        if (!CuratedGameTemplates.All.TryGetValue(request.ImageTag, out var template))
        {
            throw new UnknownGameException(request.ImageTag);
        }

        if (!_clientFactory.TryGetClient(out var client, out var error))
        {
            throw new ClusterUnreachableException($"Cluster unreachable: {error}");
        }

        return await RunAsync(async () =>
        {
            // Reject duplicate name (Req 3.5).
            try
            {
                await client!.AppsV1.ReadNamespacedDeploymentAsync(request.Name, _options.Namespace, cancellationToken: ct);
                throw new ServerAlreadyExistsException(request.Name);
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Good — does not exist yet.
            }

            var usedNodePorts = await GatherUsedNodePortsAsync(client!, ct);

            var manifests = _deploymentBuilder.Build(
                template, request, usedNodePorts, _options.Namespace, _options.SecretName);

            await CheckCapacityBeforeDeployAsync(request, template, ct);

            // Apply in dependency order: config + storage before the workload,
            // service last (design.md → Manifest Application Order).
            await client!.CoreV1.CreateNamespacedConfigMapAsync(manifests.ConfigMap, _options.Namespace, cancellationToken: ct);
            await client.CoreV1.CreateNamespacedPersistentVolumeClaimAsync(manifests.Pvc, _options.Namespace, cancellationToken: ct);
            await client.AppsV1.CreateNamespacedDeploymentAsync(manifests.Deployment, _options.Namespace, cancellationToken: ct);
            await client.CoreV1.CreateNamespacedServiceAsync(manifests.Service, _options.Namespace, cancellationToken: ct);

            _logger.LogInformation("Deployed server {Name} from image {Image}.", request.Name, template.ImageTag);

            var detail = await GetServerAsync(request.Name, ct);
            return detail ?? throw new InvalidOperationException(
                $"Server '{request.Name}' was created but could not be read back.");
        });
    }

    public async Task ScaleServerAsync(string name, int replicas, CancellationToken ct)
    {
        // Game servers are single-replica by design (Req 4.4 / Req 9).
        if (replicas is < 0 or > 1)
        {
            throw new ArgumentException(
                $"Replicas must be 0 (stop) or 1 (start); got {replicas}.", nameof(replicas));
        }

        if (!_clientFactory.TryGetClient(out var client, out var error))
        {
            throw new ClusterUnreachableException($"Cluster unreachable: {error}");
        }

        await RunAsync(async () =>
        {
            if (replicas == 1)
            {
                await CheckCapacityBeforeStartAsync(client!, name, ct);
            }

            try
            {
                var patch = new V1Patch(
                    $"{{\"spec\":{{\"replicas\":{replicas}}}}}",
                    V1Patch.PatchType.MergePatch);

                await client!.AppsV1.PatchNamespacedDeploymentScaleAsync(
                    patch, name, _options.Namespace, cancellationToken: ct);
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new ServerNotFoundException(name);
            }

            _logger.LogInformation("Scaled server {Name} to {Replicas} replica(s).", name, replicas);
            return true;
        });
    }

    public async Task DeleteServerAsync(string name, bool deleteData, CancellationToken ct)
    {
        if (!_clientFactory.TryGetClient(out var client, out var error))
        {
            throw new ClusterUnreachableException($"Cluster unreachable: {error}");
        }

        await RunAsync(async () =>
        {
            // Resolve the actual Service name by label selector before deleting the
            // Deployment, so teardown works for both dashboard-created servers
            // ({name}-service) and manually-created ones (e.g. cs2-service).
            string? serviceName = null;
            try
            {
                var deployment = await client!.AppsV1.ReadNamespacedDeploymentAsync(
                    name, _options.Namespace, cancellationToken: ct);
                var services = await client.CoreV1.ListNamespacedServiceAsync(_options.Namespace, cancellationToken: ct);
                serviceName = FindServiceForDeployment(deployment, services.Items)?.Metadata.Name;
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Deployment already gone; fall back to the naming convention below.
            }

            serviceName ??= ServiceNameFor(name);

            // Idempotent teardown: a missing resource is not an error (Req 5.4).
            await DeleteIgnoringNotFoundAsync(() =>
                client!.AppsV1.DeleteNamespacedDeploymentAsync(name, _options.Namespace, cancellationToken: ct));

            await DeleteIgnoringNotFoundAsync(() =>
                client!.CoreV1.DeleteNamespacedServiceAsync(serviceName, _options.Namespace, cancellationToken: ct));

            await DeleteIgnoringNotFoundAsync(() =>
                client!.CoreV1.DeleteNamespacedConfigMapAsync(ConfigMapNameFor(name), _options.Namespace, cancellationToken: ct));

            if (deleteData)
            {
                await DeleteIgnoringNotFoundAsync(() =>
                    client!.CoreV1.DeleteNamespacedPersistentVolumeClaimAsync(
                        PvcNameFor(name), _options.Namespace, cancellationToken: ct));
            }

            _logger.LogInformation(
                "Deleted server {Name} (deleteData={DeleteData}).", name, deleteData);
            return true;
        });
    }

    public async Task<IDictionary<string, string>> GetConfigAsync(string name, CancellationToken ct)
    {
        if (!_clientFactory.TryGetClient(out var client, out var error))
        {
            throw new ClusterUnreachableException($"Cluster unreachable: {error}");
        }

        return await RunAsync(async () =>
        {
            var configMapName = await ResolveConfigMapNameAsync(client!, name, ct);

            V1ConfigMap configMap;
            try
            {
                configMap = await client!.CoreV1.ReadNamespacedConfigMapAsync(
                    configMapName, _options.Namespace, cancellationToken: ct);
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new ServerNotFoundException(name);
            }

            var data = configMap.Data ?? new Dictionary<string, string>();

            // Defensive: never surface any key that looks sensitive, even though secret
            // values are sourced from the Secret and should not be in the ConfigMap
            // (Req 6.4, Req 14.3).
            return (IDictionary<string, string>)data
                .Where(kv => !SecretRedactor.IsSensitiveKey(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
        });
    }

    public async Task UpdateConfigAsync(string name, IDictionary<string, string> values, CancellationToken ct)
    {
        if (!_clientFactory.TryGetClient(out var client, out var error))
        {
            throw new ClusterUnreachableException($"Cluster unreachable: {error}");
        }

        await RunAsync(async () =>
        {
            var configMapName = await ResolveConfigMapNameAsync(client!, name, ct);

            V1ConfigMap configMap;
            try
            {
                configMap = await client!.CoreV1.ReadNamespacedConfigMapAsync(
                    configMapName, _options.Namespace, cancellationToken: ct);
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new ServerNotFoundException(name);
            }

            var data = configMap.Data ?? new Dictionary<string, string>();
            foreach (var (key, value) in values)
            {
                // Never allow a sensitive key to be written into the plaintext ConfigMap.
                if (SecretRedactor.IsSensitiveKey(key))
                {
                    continue;
                }
                data[key] = value;
            }
            configMap.Data = data;

            await client!.CoreV1.ReplaceNamespacedConfigMapAsync(
                configMap, configMapName, _options.Namespace, cancellationToken: ct);

            // Trigger a rolling restart so the new config is picked up (Req 6.3).
            await TriggerRollingRestartAsync(client, name, ct);

            _logger.LogInformation("Updated config for server {Name} and triggered rolling restart.", name);
            return true;
        });
    }

    private async Task<string> ResolveConfigMapNameAsync(IKubernetes client, string serverName, CancellationToken ct)
    {
        // Prefer the ConfigMap the Deployment actually references via envFrom, so this
        // works for both dashboard-created servers ({name}-config) and manually-created
        // ones (e.g. cs2-config for the cs2-server deployment). Falls back to the naming
        // convention if the deployment or reference is absent.
        try
        {
            var deployment = await client.AppsV1.ReadNamespacedDeploymentAsync(
                serverName, _options.Namespace, cancellationToken: ct);

            var container = deployment.Spec?.Template.Spec?.Containers?.FirstOrDefault();
            var refName = container?.EnvFrom?
                .Select(e => e.ConfigMapRef?.Name)
                .FirstOrDefault(n => !string.IsNullOrEmpty(n));

            if (!string.IsNullOrEmpty(refName))
            {
                return refName;
            }
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new ServerNotFoundException(serverName);
        }

        return ConfigMapNameFor(serverName);
    }

    /// <summary>
    /// Pre-deploy capacity check (Req 12.4, Task 8.3). Logs a warning if the new
    /// server's memory request would push projected usage over node capacity.
    /// Never blocks the deploy — on a personal/dev cluster, a hard rejection would
    /// be more disruptive than useful; the warning is enough to inform the user.
    /// If metrics are unavailable, the check is skipped silently (Req 9.3 pattern:
    /// absence of metrics-server should not degrade unrelated functionality).
    /// </summary>
    private async Task CheckCapacityBeforeDeployAsync(
        DeployServerRequest request, GameTemplate template, CancellationToken ct)
    {
        var resources = request.Resources ?? template.DefaultResources;
        await CheckCapacityAsync(resources.MemoryRequest, $"deploying '{request.Name}'", ct);
    }

    /// <summary>
    /// Pre-start capacity check (Req 12.4, Task 8.3) for scaling an existing,
    /// stopped server back up to 1 replica.
    /// </summary>
    private async Task CheckCapacityBeforeStartAsync(IKubernetes client, string name, CancellationToken ct)
    {
        try
        {
            var deployment = await client.AppsV1.ReadNamespacedDeploymentAsync(
                name, _options.Namespace, cancellationToken: ct);
            var memoryRequest = deployment.Spec?.Template.Spec?.Containers?.FirstOrDefault()?
                .Resources?.Requests?.TryGetValue("memory", out var memQuantity) == true
                ? memQuantity.ToString() : null;

            if (!string.IsNullOrEmpty(memoryRequest))
            {
                await CheckCapacityAsync(memoryRequest, $"starting '{name}'", ct);
            }
        }
        catch (HttpOperationException)
        {
            // Deployment lookup failures here are non-fatal to the capacity check;
            // ScaleServerAsync's own NotFound handling covers the real error path.
        }
    }

    private async Task CheckCapacityAsync(string memoryRequestQuantity, string action, CancellationToken ct)
    {
        var snapshot = await _metricsService.GetSnapshotAsync(ct);
        if (!snapshot.Available || snapshot.Node is null)
        {
            return; // nothing meaningful to check without usage data
        }

        var additionalBytes = ParseMemoryBytesForCapacityCheck(memoryRequestQuantity);
        var result = AutoScaleDecisionEngine.CheckCapacity(
            metricsAvailable: snapshot.Available,
            currentMemoryUsedBytes: snapshot.Node.MemUsedBytes,
            memoryCapacityBytes: snapshot.Node.MemCapacityBytes,
            additionalMemoryRequestBytes: additionalBytes);

        if (result == AutoScaleDecisionEngine.CapacityCheckResult.Warn)
        {
            _logger.LogWarning(
                "Capacity warning: {Action} would project node memory usage above capacity " +
                "(current {UsedBytes} + requested {AdditionalBytes} > capacity {CapacityBytes}).",
                action, snapshot.Node.MemUsedBytes, additionalBytes, snapshot.Node.MemCapacityBytes);
        }
    }

    private static double ParseMemoryBytesForCapacityCheck(string value)
    {
        if (value.EndsWith("Gi")) return double.Parse(value[..^2]) * 1024 * 1024 * 1024;
        if (value.EndsWith("Mi")) return double.Parse(value[..^2]) * 1024 * 1024;
        if (value.EndsWith("Ki")) return double.Parse(value[..^2]) * 1024;
        return double.TryParse(value, out var raw) ? raw : 0;
    }

    private async Task<IReadOnlySet<int>> GatherUsedNodePortsAsync(IKubernetes client, CancellationToken ct)
    {
        var services = await client.CoreV1.ListNamespacedServiceAsync(_options.Namespace, cancellationToken: ct);
        var used = new HashSet<int>();
        foreach (var svc in services.Items)
        {
            if (svc.Spec?.Ports is null) continue;
            foreach (var port in svc.Spec.Ports)
            {
                if (port.NodePort is { } np)
                {
                    used.Add(np);
                }
            }
        }
        return used;
    }

    private async Task TriggerRollingRestartAsync(IKubernetes client, string name, CancellationToken ct)
    {
        var timestamp = DateTime.UtcNow.ToString("o");
        var patch = new V1Patch(
            $"{{\"spec\":{{\"template\":{{\"metadata\":{{\"annotations\":{{\"kubectl.kubernetes.io/restartedAt\":\"{timestamp}\"}}}}}}}}}}",
            V1Patch.PatchType.MergePatch);

        await client.AppsV1.PatchNamespacedDeploymentAsync(patch, name, _options.Namespace, cancellationToken: ct);
    }

    private static async Task DeleteIgnoringNotFoundAsync(Func<Task> deleteAction)
    {
        try
        {
            await deleteAction();
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Idempotent: already gone.
        }
    }

    // --- mapping helpers ---

    // Naming helpers matching DeploymentBuilderService's convention.
    private static string ConfigMapNameFor(string serverName) => $"{serverName}-config";
    private static string PvcNameFor(string serverName) => $"{serverName}-data";
    private static string ServiceNameFor(string serverName) => $"{serverName}-service";

    private static V1Service? FindServiceForDeployment(V1Deployment deployment, IList<V1Service> allServices)
    {
        var appLabel = deployment.Spec?.Selector?.MatchLabels?.TryGetValue(AppLabel, out var lbl) == true ? lbl : null;
        if (string.IsNullOrEmpty(appLabel))
        {
            return null;
        }

        // Services are matched by their spec.selector (not metadata labels) targeting
        // the same "app" label the Deployment's pods carry — this is how Kubernetes
        // itself wires a Service to a Deployment, and avoids depending on any
        // particular Service naming convention.
        return allServices.FirstOrDefault(s =>
            s.Spec?.Selector != null &&
            s.Spec.Selector.TryGetValue(AppLabel, out var v) &&
            v == appLabel);
    }

    private static IReadOnlyList<V1Pod> FindPodsForDeployment(V1Deployment deployment, IList<V1Pod> allPods)
    {
        var appLabel = deployment.Spec?.Selector?.MatchLabels?.TryGetValue(AppLabel, out var lbl) == true ? lbl : null;
        if (string.IsNullOrEmpty(appLabel))
        {
            return Array.Empty<V1Pod>();
        }

        return allPods
            .Where(p => p.Metadata.Labels != null &&
                        p.Metadata.Labels.TryGetValue(AppLabel, out var v) &&
                        v == appLabel)
            .ToList();
    }

    private static ServerSummary MapToSummary(V1Deployment deployment, IReadOnlyList<V1Pod> pods)
    {
        var replicas = deployment.Spec?.Replicas ?? 0;
        var status = MapStatus(replicas, pods);
        var container = deployment.Spec?.Template.Spec?.Containers?.FirstOrDefault();

        return new ServerSummary(
            Name: deployment.Metadata.Name,
            Game: deployment.Metadata.Labels?.TryGetValue(AppLabel, out var g) == true ? g : deployment.Metadata.Name,
            Image: container?.Image ?? "unknown",
            Status: status,
            Replicas: replicas,
            CreatedAt: deployment.Metadata.CreationTimestamp ?? DateTime.MinValue);
    }

    private static ServerDetail MapToDetail(V1Deployment deployment, IReadOnlyList<V1Pod> pods, V1Service? service)
    {
        var summary = MapToSummary(deployment, pods);
        var container = deployment.Spec?.Template.Spec?.Containers?.FirstOrDefault();

        var ports = service?.Spec?.Ports?.Select(p => new PortMapping(
            Name: p.Name ?? string.Empty,
            Protocol: p.Protocol ?? "TCP",
            ContainerPort: p.TargetPort?.Value is string s && int.TryParse(s, out var tp) ? tp : (int)(p.Port),
            NodePort: p.NodePort ?? 0)).ToList()
            ?? new List<PortMapping>();

        var resources = container?.Resources is { } r
            ? new ResourceSpec(
                CpuRequest: GetQuantity(r.Requests, "cpu"),
                CpuLimit: GetQuantity(r.Limits, "cpu"),
                MemoryRequest: GetQuantity(r.Requests, "memory"),
                MemoryLimit: GetQuantity(r.Limits, "memory"))
            : null;

        return new ServerDetail(
            Name: summary.Name,
            Game: summary.Game,
            Image: summary.Image,
            Status: summary.Status,
            Replicas: summary.Replicas,
            CreatedAt: summary.CreatedAt,
            Ports: ports,
            Resources: resources,
            Players: null); // populated once RconService lands in Phase 6
    }

    private static string GetQuantity(IDictionary<string, k8s.Models.ResourceQuantity>? dict, string key)
    {
        if (dict != null && dict.TryGetValue(key, out var quantity))
        {
            return quantity.ToString();
        }
        return string.Empty;
    }

    /// <summary>
    /// Status derivation rules (Req 2.3, design.md):
    ///   replicas == 0                        → Stopped
    ///   replicas &gt; 0, no pod found         → Pending
    ///   replicas &gt; 0, pod phase Running
    ///     and all containers ready            → Running
    ///   replicas &gt; 0, pod phase Pending      → Pending
    ///   replicas &gt; 0, pod phase Failed/
    ///     Running-but-not-ready/restarting     → Error
    ///   anything else                         → Unknown
    /// </summary>
    private static ServerStatus MapStatus(int replicas, IReadOnlyList<V1Pod> pods)
    {
        if (replicas == 0)
        {
            return ServerStatus.Stopped;
        }

        var pod = pods.FirstOrDefault();
        if (pod is null)
        {
            return ServerStatus.Pending;
        }

        var phase = pod.Status?.Phase;
        return phase switch
        {
            "Running" => AllContainersReady(pod) ? ServerStatus.Running : ServerStatus.Error,
            "Pending" => ServerStatus.Pending,
            "Failed" => ServerStatus.Error,
            "Succeeded" => ServerStatus.Unknown,
            _ => ServerStatus.Unknown
        };
    }

    private static bool AllContainersReady(V1Pod pod)
    {
        var statuses = pod.Status?.ContainerStatuses;
        if (statuses is null || statuses.Count == 0)
        {
            return false;
        }

        return statuses.All(c => c.Ready);
    }
}
