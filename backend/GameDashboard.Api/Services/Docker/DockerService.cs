using System.Security.Cryptography;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using GameDashboard.Api.Configuration;
using GameDashboard.Api.Exceptions;
using GameDashboard.Api.GameTemplates;
using GameDashboard.Api.Logging;
using GameDashboard.Api.Models;
using Microsoft.Extensions.Options;

namespace GameDashboard.Api.Services.Docker;

/// <summary>
/// See <see cref="IServerOrchestrator"/> and docs/docker-migration.md. Game
/// servers are plain Docker containers: one named container + one named volume
/// per server, with the dashboard's metadata carried in
/// <see cref="ContainerLabels"/> labels and mutable state (secrets,
/// last-active) in local files.
/// </summary>
public sealed class DockerService : IServerOrchestrator
{
    /// <summary>Graceful stop window so game servers can save their world/state.</summary>
    private const uint StopTimeoutSeconds = 30;

    private readonly IDockerClientFactory _clientFactory;
    private readonly IContainerSpecBuilder _specBuilder;
    private readonly DeployTracker _deployTracker;
    private readonly ISecretsStore _secretsStore;
    private readonly ILastActiveStore _lastActiveStore;
    private readonly ITcpReadinessProber _prober;
    private readonly IRconService _rconService;
    private readonly IMetricsService _metricsService;
    private readonly IMinecraftMetadataService _minecraftMetadata;
    private readonly DashboardOptions _options;
    private readonly ILogger<DockerService> _logger;

    public DockerService(
        IDockerClientFactory clientFactory,
        IContainerSpecBuilder specBuilder,
        DeployTracker deployTracker,
        ISecretsStore secretsStore,
        ILastActiveStore lastActiveStore,
        ITcpReadinessProber prober,
        IRconService rconService,
        IMetricsService metricsService,
        IMinecraftMetadataService minecraftMetadata,
        IOptions<DashboardOptions> options,
        ILogger<DockerService> logger)
    {
        _clientFactory = clientFactory;
        _specBuilder = specBuilder;
        _deployTracker = deployTracker;
        _secretsStore = secretsStore;
        _lastActiveStore = lastActiveStore;
        _prober = prober;
        _rconService = rconService;
        _metricsService = metricsService;
        _minecraftMetadata = minecraftMetadata;
        _options = options.Value;
        _logger = logger;
    }

    // --- health / setup ---

    public async Task<ClusterHealth> GetHealthAsync(CancellationToken ct)
    {
        // Wire shape kept from the k8s era: ClusterReachable now means "Docker
        // engine reachable"; NamespaceReady mirrors it (there is no namespace
        // to create); Namespace is a display-only label value.
        var (client, error) = await _clientFactory.TryGetClientAsync(ct);
        if (client is null)
        {
            return new ClusterHealth(false, false, _options.Namespace, error);
        }

        try
        {
            await client.System.PingAsync(ct);
            return new ClusterHealth(true, true, _options.Namespace, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Docker engine reachability check failed.");
            return new ClusterHealth(false, false, _options.Namespace, ex.Message);
        }
    }

    public async Task<SetupStatus> GetSetupStatusAsync(CancellationToken ct)
    {
        var warnings = new List<string>();

        var health = await GetHealthAsync(ct);
        if (!health.ClusterReachable)
        {
            warnings.Add(
                "Docker engine is unreachable. Start the bundled runtime from this screen, " +
                "or start Docker Desktop if you use it.");
        }

        // With Kubernetes, resource metrics needed the optional metrics-server;
        // docker stats is built into the engine, so metrics are available
        // exactly when the engine is.
        var metricsAvailable = health.ClusterReachable;

        return new SetupStatus(
            DockerEngineReachable: health.ClusterReachable,
            MetricsAvailable: metricsAvailable,
            Warnings: warnings);
    }

    // --- per-server secrets (local encrypted store, namespaced by server) ---

    public async Task<IReadOnlyList<ServerSecretInfo>> GetServerSecretsAsync(string name, CancellationToken ct)
    {
        var client = await RequireClientAsync(ct);
        return await RunAsync(async () =>
        {
            var container = await InspectOrThrowAsync(client, name, ct);
            var storeKeys = SecretKeyRefsOf(container.Config?.Labels).Values
                .Distinct(StringComparer.Ordinal)
                .OrderBy(k => k, StringComparer.Ordinal);
            var managed = ManagedSecretKeysOf(container.Config?.Labels);

            var result = new List<ServerSecretInfo>();
            foreach (var key in storeKeys)
            {
                var value = await _secretsStore.GetValueAsync(ServerSecretKey.Scope(name, key), ct);
                result.Add(new ServerSecretInfo(key, value is not null, managed.Contains(key)));
            }
            return (IReadOnlyList<ServerSecretInfo>)result;
        });
    }

    public async Task SetServerSecretsAsync(string name, IDictionary<string, string> values, CancellationToken ct)
    {
        var client = await RequireClientAsync(ct);
        await RunAsync(async () =>
        {
            var container = await InspectOrThrowAsync(client, name, ct);
            var allowed = new HashSet<string>(
                SecretKeyRefsOf(container.Config?.Labels).Values, StringComparer.Ordinal);

            foreach (var key in values.Keys)
            {
                if (!allowed.Contains(key))
                {
                    throw new ArgumentException(
                        $"'{key}' is not a secret used by server '{name}'. " +
                        $"Allowed keys: {string.Join(", ", allowed.OrderBy(k => k, StringComparer.Ordinal))}.");
                }
            }

            // Store under the per-server scope, then recreate the container so
            // the new value is injected (Docker can't change env in place).
            var scoped = values.ToDictionary(
                kv => ServerSecretKey.Scope(name, kv.Key), kv => kv.Value, StringComparer.Ordinal);
            await _secretsStore.SetAsync(scoped, ct);

            await RecreateWithSecretsAsync(client, name, container, ConfigOf(container.Config!.Labels!), ct);
            return true;
        });
    }

    public async Task<string> GetServerSecretValueAsync(string name, string key, CancellationToken ct)
    {
        // Reads the store directly (no engine call needed): the value leaves the
        // process only in the HTTP response to this explicit reveal request.
        return await _secretsStore.GetValueAsync(ServerSecretKey.Scope(name, key), ct)
            ?? throw new KeyNotFoundException($"No value is set for secret '{key}' on server '{name}'.");
    }

    public async Task DeleteServerSecretAsync(string name, string key, CancellationToken ct)
    {
        var client = await RequireClientAsync(ct);
        await RunAsync(async () =>
        {
            var container = await InspectOrThrowAsync(client, name, ct);
            if (!await _secretsStore.DeleteAsync(ServerSecretKey.Scope(name, key), ct))
            {
                throw new KeyNotFoundException($"No value is set for secret '{key}' on server '{name}'.");
            }

            await RecreateWithSecretsAsync(client, name, container, ConfigOf(container.Config!.Labels!), ct);
            return true;
        });
    }

    public async Task RegenerateServerSecretAsync(string name, string key, CancellationToken ct)
    {
        var client = await RequireClientAsync(ct);
        await RunAsync(async () =>
        {
            var container = await InspectOrThrowAsync(client, name, ct);
            if (!ManagedSecretKeysOf(container.Config?.Labels).Contains(key))
            {
                // Only app-managed secrets can be regenerated — a user-supplied
                // secret (e.g. a Steam GSLT) has no meaningful value we can mint.
                throw new ArgumentException(
                    $"'{key}' is not an app-managed secret on server '{name}', so it can't be regenerated. " +
                    "Set its value directly instead.");
            }

            var scoped = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ServerSecretKey.Scope(name, key)] = GenerateSecretValue(),
            };
            await _secretsStore.SetAsync(scoped, ct);

            await RecreateWithSecretsAsync(client, name, container, ConfigOf(container.Config!.Labels!), ct);
            return true;
        });
    }

    // --- last-active (local file) ---

    public async Task<DateTimeOffset> GetLastActiveAsync(string name, CancellationToken ct)
    {
        var recorded = await _lastActiveStore.GetAsync(name, ct);
        if (recorded is not null)
        {
            return recorded.Value;
        }

        // Nothing recorded yet: treat the server as active as of its creation
        // rather than infinitely stale, so a freshly-deployed server isn't
        // immediately the top scale-down candidate.
        var client = await RequireClientAsync(ct);
        var container = await InspectOrThrowAsync(client, name, ct);
        return new DateTimeOffset(CreatedAtOf(container.Config?.Labels, container.Created));
    }

    public async Task SetLastActiveAsync(string name, DateTimeOffset timestamp, CancellationToken ct)
    {
        await _lastActiveStore.SetAsync(name, timestamp, ct);
    }

    // --- reads ---

    public async Task<IReadOnlyList<ServerSummary>> ListServersAsync(CancellationToken ct)
    {
        var client = await RequireClientAsync(ct);

        return await RunAsync(async () =>
        {
            var containers = await ListManagedContainersAsync(client, ct);

            var summaries = new List<ServerSummary>(containers.Count);
            foreach (var container in containers)
            {
                summaries.Add(await MapToSummaryAsync(container, ct));
            }

            // Deploys still pulling their image have no container yet; merge
            // tracker entries so they're visible the moment deploy returns.
            var existingNames = summaries.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var pending in _deployTracker.All)
            {
                if (!existingNames.Contains(pending.Name))
                {
                    summaries.Add(_deployTracker.ToSummary(pending));
                }
            }

            return (IReadOnlyList<ServerSummary>)summaries;
        });
    }

    public async Task<ServerDetail?> GetServerAsync(string name, CancellationToken ct)
    {
        var client = await RequireClientAsync(ct);

        return await RunAsync(async () =>
        {
            ContainerInspectResponse container;
            try
            {
                container = await client.Containers.InspectContainerAsync(name, ct);
            }
            catch (DockerContainerNotFoundException)
            {
                var pending = _deployTracker.Get(name);
                return pending is null ? null : (ServerDetail?)_deployTracker.ToDetail(pending);
            }

            if (!IsManaged(container.Config?.Labels))
            {
                return null; // not ours — don't surface arbitrary user containers
            }

            var detail = await MapToDetailAsync(container, ct);

            // Only worth querying RCON for a server that is actually up;
            // querying a stopped server would just wait out a connect timeout.
            if (detail.Status == ServerStatus.Running)
            {
                var players = await _rconService.QueryPlayerInfoAsync(name, ct);
                detail = detail with { Players = players };
            }

            return (ServerDetail?)detail;
        });
    }

    public async Task<IDictionary<string, string>> GetConfigAsync(string name, CancellationToken ct)
    {
        var client = await RequireClientAsync(ct);

        return await RunAsync(async () =>
        {
            IReadOnlyDictionary<string, string> config;
            try
            {
                var container = await client.Containers.InspectContainerAsync(name, ct);
                EnsureManaged(name, container.Config?.Labels);
                config = ConfigOf(container.Config?.Labels);
            }
            catch (DockerContainerNotFoundException)
            {
                var pending = _deployTracker.Get(name) ?? throw new ServerNotFoundException(name);
                config = pending.Config;
            }

            // Defensive: never surface any key that looks sensitive, even though
            // secret values live in the secrets store and are injected as env
            // only (they are never written into the config label).
            return (IDictionary<string, string>)config
                .Where(kv => !SecretRedactor.IsSensitiveKey(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
        });
    }

    // --- deploy (asynchronous: validate + reserve now, pull/create/start in background) ---

    public async Task<ServerDetail> DeployServerAsync(DeployServerRequest request, CancellationToken ct)
    {
        ServerNameValidator.EnsureValid(request.Name);

        if (!CuratedGameTemplates.All.TryGetValue(request.ImageTag, out var template))
        {
            throw new UnknownGameException(request.ImageTag);
        }

        var client = await RequireClientAsync(ct);

        return await RunAsync(async () =>
        {
            // Reject duplicate name — any existing container claims the name at
            // the engine level, managed or not.
            try
            {
                await client.Containers.InspectContainerAsync(request.Name, ct);
                throw new ServerAlreadyExistsException(request.Name);
            }
            catch (DockerContainerNotFoundException)
            {
                // Good — does not exist yet.
            }

            // User-supplied secrets are entered on the server's page after it
            // exists, so a deploy no longer requires them: app-managed secrets
            // (e.g. RCON passwords) are generated here, and whatever user secrets
            // are already set are baked in. When a user secret is still missing
            // the container is created but left stopped rather than starting a
            // server that can't work.
            var (secretEnv, missingSecrets) = await ResolveSecretEnvAsync(
                request.Name, template.SecretKeyRefs,
                template.ManagedSecretKeysOrEmpty.ToHashSet(StringComparer.Ordinal), ct);
            var startAfterCreate = missingSecrets.Count == 0;

            var modpackMcVersion = await ResolveModpackMinecraftVersionAsync(template, request, ct);
            var usedPorts = await GatherUsedHostPortsAsync(client, ct);
            var spec = _specBuilder.Build(template, request, usedPorts, secretEnv, modpackMcVersion);

            var pending = new DeployTracker.PendingDeploy(
                request.Name, template.ImageTag, spec.Image,
                spec.Ports, spec.Resources, spec.Config, DateTime.UtcNow);

            if (!_deployTracker.TryRegister(pending))
            {
                throw new ServerAlreadyExistsException(request.Name);
            }

            await CheckCapacityAsync(spec.Resources.MemoryRequest, $"deploying '{request.Name}'", ct);

            // The pull can take minutes on first deploy; run it detached from
            // the HTTP request. The tracker keeps the server visible meanwhile.
            _ = Task.Run(() => RunDeployInBackgroundAsync(client, spec, startAfterCreate), CancellationToken.None);

            _logger.LogInformation(
                "Accepted deploy of server {Name} from image {Image}; pull/create running in background.",
                request.Name, spec.Image);

            return _deployTracker.ToDetail(pending);
        });
    }

    /// <summary>
    /// For a Minecraft modpack deploy, asks Modrinth which Minecraft version
    /// the pack pins so the spec builder can pick the itzg image with a JVM
    /// that can run it (a 26.x pack crashes the java21 image with
    /// UnsupportedClassVersionError). Null on any failure — the builder then
    /// falls back to java21 rather than blocking the deploy.
    /// </summary>
    private async Task<string?> ResolveModpackMinecraftVersionAsync(
        GameTemplate template, DeployServerRequest request, CancellationToken ct)
    {
        if (template.Kind != TemplateKind.MinecraftJava)
        {
            return null;
        }

        string? pack = null;
        if (request.ConfigOverrides?.TryGetValue("MODRINTH_MODPACK", out var fromOverrides) == true)
        {
            pack = fromOverrides;
        }
        else if (template.DefaultConfig.TryGetValue("MODRINTH_MODPACK", out var fromDefaults))
        {
            pack = fromDefaults;
        }

        if (string.IsNullOrWhiteSpace(pack))
        {
            return null;
        }

        var version = await _minecraftMetadata.TryGetModpackMinecraftVersionAsync(pack, ct);
        if (version is null)
        {
            _logger.LogWarning(
                "Could not resolve the Minecraft version of modpack {Pack}; deploying on the java21 fallback image.",
                pack);
        }
        else
        {
            _logger.LogInformation("Modpack {Pack} targets Minecraft {Version}.", pack, version);
        }

        return version;
    }

    private async Task RunDeployInBackgroundAsync(
        IDockerClient client, ContainerServerSpec spec, bool startAfterCreate)
    {
        try
        {
            await PullImageAsync(client, spec.Image, CancellationToken.None);

            // Named-volume create is idempotent — re-deploying a name whose
            // volume survived (keep-data delete) simply reattaches the world.
            await client.Volumes.CreateAsync(new VolumesCreateParameters
            {
                Name = spec.VolumeName,
                Labels = new Dictionary<string, string>
                {
                    [ContainerLabels.Managed] = ContainerLabels.ManagedValue,
                    [ContainerLabels.Game] = spec.Name,
                },
            }, CancellationToken.None);

            await client.Containers.CreateContainerAsync(spec.CreateParameters, CancellationToken.None);

            if (startAfterCreate)
            {
                await client.Containers.StartContainerAsync(
                    spec.Name, new ContainerStartParameters(), CancellationToken.None);
            }

            _deployTracker.Remove(spec.Name);
            _logger.LogInformation(
                "Deployed server {Name} from image {Image} (started={Started}).",
                spec.Name, spec.Image, startAfterCreate);
        }
        catch (Exception ex)
        {
            _deployTracker.MarkFailed(spec.Name, ex.Message);
            _logger.LogError(ex, "Background deploy of server {Name} failed.", spec.Name);
        }
    }

    private static async Task PullImageAsync(IDockerClient client, string image, CancellationToken ct)
    {
        var (fromImage, tag) = SplitImageRef(image);
        await client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = fromImage, Tag = tag },
            authConfig: null,
            progress: new Progress<JSONMessage>(),
            ct);
    }

    /// <summary>"itzg/minecraft-server:java21" → ("itzg/minecraft-server", "java21"); a ref without a tag defaults to "latest".</summary>
    internal static (string Image, string Tag) SplitImageRef(string imageRef)
    {
        var lastColon = imageRef.LastIndexOf(':');
        var lastSlash = imageRef.LastIndexOf('/');
        return lastColon > lastSlash
            ? (imageRef[..lastColon], imageRef[(lastColon + 1)..])
            : (imageRef, "latest");
    }

    // --- scale / delete / config update ---

    public async Task ScaleServerAsync(string name, int replicas, CancellationToken ct)
    {
        // Game servers are single-replica by design; the wire contract keeps
        // the k8s-era semantics of 0 = stop, 1 = start.
        if (replicas is < 0 or > 1)
        {
            throw new ArgumentException(
                $"Replicas must be 0 (stop) or 1 (start); got {replicas}.", nameof(replicas));
        }

        var client = await RequireClientAsync(ct);

        await RunAsync(async () =>
        {
            var container = await InspectOrThrowAsync(client, name, ct);

            if (replicas == 1)
            {
                // App-managed secrets are generated on the fly here; user-supplied
                // secrets are entered on the server's page after deploy. Refuse to
                // start (409) until every user secret is set, naming what's missing.
                var (_, missingSecrets) = await ResolveSecretEnvAsync(
                    name, SecretKeyRefsOf(container.Config?.Labels),
                    ManagedSecretKeysOf(container.Config?.Labels), ct);
                if (missingSecrets.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Can't start '{name}': set the required secret(s) " +
                        $"{string.Join(", ", missingSecrets)} on the server's Secrets tab first.");
                }

                var resources = ResourcesOf(container.Config?.Labels);
                if (!string.IsNullOrEmpty(resources?.MemoryRequest))
                {
                    await CheckCapacityAsync(resources.MemoryRequest, $"starting '{name}'", ct);
                }

                await client.Containers.StartContainerAsync(name, new ContainerStartParameters(), ct);
            }
            else
            {
                await client.Containers.StopContainerAsync(
                    name, new ContainerStopParameters { WaitBeforeKillSeconds = StopTimeoutSeconds }, ct);
            }

            _logger.LogInformation("Scaled server {Name} to {Replicas} replica(s).", name, replicas);
            return true;
        });
    }

    public async Task DeleteServerAsync(string name, bool deleteData, CancellationToken ct)
    {
        // Clears a Pending/Error deploy entry as well, so a stuck deploy can
        // always be cleaned up from the UI.
        _deployTracker.Remove(name);

        var client = await RequireClientAsync(ct);

        await RunAsync(async () =>
        {
            // Idempotent teardown: a missing resource is not an error.
            try
            {
                await client.Containers.RemoveContainerAsync(
                    name, new ContainerRemoveParameters { Force = true }, ct);
            }
            catch (DockerContainerNotFoundException)
            {
                // Already gone.
            }

            if (deleteData)
            {
                try
                {
                    await client.Volumes.RemoveAsync(ContainerSpecBuilder.VolumeNameFor(name), force: true, ct);
                }
                catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Already gone.
                }
            }

            await _lastActiveStore.RemoveAsync(name, ct);
            await RemoveServerSecretsAsync(name, ct);

            _logger.LogInformation("Deleted server {Name} (deleteData={DeleteData}).", name, deleteData);
            return true;
        });
    }

    /// <summary>
    /// Drops every secret scoped to a server so a later deploy that reuses the
    /// name starts from a clean slate rather than inheriting stale values.
    /// </summary>
    private async Task RemoveServerSecretsAsync(string name, CancellationToken ct)
    {
        var prefix = ServerSecretKey.Prefix(name);
        foreach (var key in await _secretsStore.GetKeysAsync(ct))
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                await _secretsStore.DeleteAsync(key, ct);
            }
        }
    }

    public async Task UpdateConfigAsync(string name, IDictionary<string, string> values, CancellationToken ct)
    {
        var client = await RequireClientAsync(ct);

        await RunAsync(async () =>
        {
            var container = await InspectOrThrowAsync(client, name, ct);
            EnsureManaged(name, container.Config?.Labels);
            var labels = container.Config!.Labels!;

            var config = new Dictionary<string, string>(ConfigOf(labels));
            foreach (var (key, value) in values)
            {
                // Never allow a sensitive key to be written into the plaintext
                // config label — secret values only ever come from the store.
                if (SecretRedactor.IsSensitiveKey(key))
                {
                    continue;
                }
                config[key] = value;
            }

            await RecreateWithSecretsAsync(client, name, container, config, ct);

            _logger.LogInformation("Updated config for server {Name} and recreated its container.", name);
            return true;
        });
    }

    /// <summary>
    /// Recreates a server's container from its current labels with the given
    /// config and freshly-resolved secret env, preserving its running state.
    /// Docker can't change env in place, so config and secret changes both go
    /// through this recreate. Secret resolution is lenient (bakes in whatever is
    /// set) — start is the gate that enforces required secrets.
    /// </summary>
    private async Task RecreateWithSecretsAsync(
        IDockerClient client,
        string name,
        ContainerInspectResponse container,
        IReadOnlyDictionary<string, string> config,
        CancellationToken ct)
    {
        var labels = container.Config!.Labels!;
        var ports = PortsOf(labels);
        var (secretEnv, _) = await ResolveSecretEnvAsync(
            name, SecretKeyRefsOf(labels), ManagedSecretKeysOf(labels), ct);
        var wasRunning = container.State?.Running == true || container.State?.Status == "restarting";

        var newLabels = new Dictionary<string, string>(labels)
        {
            [ContainerLabels.Config] = JsonSerializer.Serialize(config),
        };

        var create = new CreateContainerParameters
        {
            Name = name,
            Image = container.Config.Image,
            Env = config.Select(kv => $"{kv.Key}={kv.Value}")
                .Concat(secretEnv.Select(kv => $"{kv.Key}={kv.Value}"))
                .ToList(),
            Labels = newLabels,
            ExposedPorts = ports.ToDictionary(
                p => $"{p.ContainerPort}/{p.Protocol.ToLowerInvariant()}",
                _ => default(EmptyStruct)),
            HostConfig = new HostConfig
            {
                PortBindings = ports.ToDictionary(
                    p => $"{p.ContainerPort}/{p.Protocol.ToLowerInvariant()}",
                    p => (IList<PortBinding>)new List<PortBinding>
                    {
                        new() { HostPort = p.NodePort.ToString() }
                    }),
                Mounts = new List<Mount>
                {
                    new()
                    {
                        Type = "volume",
                        Source = ContainerSpecBuilder.VolumeNameFor(name),
                        Target = labels.TryGetValue(ContainerLabels.DataMount, out var mount) ? mount : "/data",
                    }
                },
                RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
                Memory = container.HostConfig?.Memory ?? 0,
                MemoryReservation = container.HostConfig?.MemoryReservation ?? 0,
                NanoCPUs = container.HostConfig?.NanoCPUs ?? 0,
            },
        };

        await client.Containers.RemoveContainerAsync(
            name, new ContainerRemoveParameters { Force = true }, ct);
        await client.Containers.CreateContainerAsync(create, ct);
        if (wasRunning)
        {
            await client.Containers.StartContainerAsync(name, new ContainerStartParameters(), ct);
        }
    }

    // --- shared plumbing ---

    private async Task<IDockerClient> RequireClientAsync(CancellationToken ct)
    {
        var (client, error) = await _clientFactory.TryGetClientAsync(ct);
        return client ?? throw new ClusterUnreachableException($"Docker engine unreachable: {error}");
    }

    /// <summary>
    /// Runs an engine call and translates low-level connectivity failures into
    /// <see cref="ClusterUnreachableException"/> so callers/middleware report
    /// 503 rather than a generic 500. DockerApiException (a real HTTP response
    /// from a reachable engine, e.g. 404/409) propagates as-is.
    /// </summary>
    private static async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (DockerApiException)
        {
            throw;
        }
        // ObjectDisposedException: the factory disposes a cached client whose
        // engine stopped answering; a request racing that teardown is just
        // another face of "engine went away".
        catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException
            or IOException or TimeoutException or TaskCanceledException or ObjectDisposedException)
        {
            throw new ClusterUnreachableException($"Docker engine unreachable: {ex.Message}");
        }
    }

    private static async Task<IList<ContainerListResponse>> ListManagedContainersAsync(
        IDockerClient client, CancellationToken ct)
    {
        return await client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool> { [ContainerLabels.ManagedFilter] = true },
            },
        }, ct);
    }

    private async Task<ContainerInspectResponse> InspectOrThrowAsync(
        IDockerClient client, string name, CancellationToken ct)
    {
        try
        {
            var container = await client.Containers.InspectContainerAsync(name, ct);
            EnsureManaged(name, container.Config?.Labels);
            return container;
        }
        catch (DockerContainerNotFoundException)
        {
            // A deploy still in flight is not startable/stoppable yet; surface
            // that clearly instead of a confusing 404.
            var pending = _deployTracker.Get(name);
            if (pending is not null && !pending.Failed)
            {
                throw new InvalidOperationException(
                    $"Server '{name}' is still deploying; wait for it to finish (or delete it).");
            }

            throw new ServerNotFoundException(name);
        }
    }

    private static bool IsManaged(IDictionary<string, string>? labels) =>
        labels is not null &&
        labels.TryGetValue(ContainerLabels.Managed, out var managed) &&
        managed == ContainerLabels.ManagedValue;

    private static void EnsureManaged(string name, IDictionary<string, string>? labels)
    {
        if (!IsManaged(labels))
        {
            // A same-named container that the dashboard didn't create is
            // invisible to the API — treat as not found rather than mutating it.
            throw new ServerNotFoundException(name);
        }
    }

    /// <summary>
    /// Resolves a server's secret env vars from the per-server scope of the
    /// store. App-managed store keys (<paramref name="managedKeys"/>) missing a
    /// value are generated here and persisted, so they never count as missing —
    /// a server whose only secrets are managed always resolves complete. Never
    /// throws on a missing user secret: it returns which store keys had none, so
    /// callers decide what to do (deploy/recreate bake in whatever is set; start
    /// refuses until nothing is missing).
    /// </summary>
    private async Task<(Dictionary<string, string> Env, List<string> Missing)> ResolveSecretEnvAsync(
        string serverName, IReadOnlyDictionary<string, string> secretKeyRefs,
        IReadOnlySet<string> managedKeys, CancellationToken ct)
    {
        var env = new Dictionary<string, string>();
        var missing = new List<string>();
        var generated = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (envName, storeKey) in secretKeyRefs)
        {
            var value = await _secretsStore.GetValueAsync(ServerSecretKey.Scope(serverName, storeKey), ct);
            if (value is null && managedKeys.Contains(storeKey))
            {
                // App-managed (e.g. an RCON password): mint a strong value the
                // first time it's needed and remember it, so the user is never
                // asked for it and the server can start on its own.
                value = GenerateSecretValue();
                generated[ServerSecretKey.Scope(serverName, storeKey)] = value;
            }

            if (value is null)
            {
                missing.Add(storeKey);
            }
            else
            {
                env[envName] = value;
            }
        }

        if (generated.Count > 0)
        {
            await _secretsStore.SetAsync(generated, ct);
        }

        return (env, missing);
    }

    /// <summary>
    /// The app-managed store keys for a server, derived from its game template
    /// (resolved via the image-tag label). Every managed container carries that
    /// label, so existing servers pick up managed-secret handling automatically;
    /// a container whose template no longer resolves yields none (safe: its
    /// secrets are then treated as user-supplied and gate start as before).
    /// </summary>
    private static IReadOnlySet<string> ManagedSecretKeysOf(IDictionary<string, string>? labels)
    {
        if (labels is not null &&
            labels.TryGetValue(ContainerLabels.ImageTag, out var tag) &&
            CuratedGameTemplates.ResolveByTag(tag) is { } template)
        {
            return new HashSet<string>(template.ManagedSecretKeysOrEmpty, StringComparer.Ordinal);
        }

        return new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Generates a strong, URL-safe random secret (e.g. an RCON password). 24
    /// random bytes → 32 base64url chars, unambiguous in env vars and RCON.
    /// </summary>
    private static string GenerateSecretValue()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static async Task<IReadOnlySet<int>> GatherUsedHostPortsAsync(
        IDockerClient client, CancellationToken ct)
    {
        // All published host ports on the engine count as taken, not just ours —
        // a bind collision fails container start regardless of who owns the port.
        var containers = await client.Containers.ListContainersAsync(
            new ContainersListParameters { All = true }, ct);

        var used = new HashSet<int>();
        foreach (var container in containers)
        {
            if (container.Ports is null) continue;
            foreach (var port in container.Ports)
            {
                if (port.PublicPort > 0)
                {
                    used.Add(port.PublicPort);
                }
            }
        }
        return used;
    }

    private async Task CheckCapacityAsync(string memoryRequestQuantity, string action, CancellationToken ct)
    {
        // Warn-only capacity check, kept from the k8s implementation: on a
        // personal machine a hard rejection would be more disruptive than
        // useful. Skipped silently when metrics are unavailable.
        var snapshot = await _metricsService.GetSnapshotAsync(ct);
        if (!snapshot.Available || snapshot.Node is null)
        {
            return;
        }

        var additionalBytes = (double)ContainerSpecBuilder.ParseMemoryBytes(memoryRequestQuantity);
        var result = AutoScaleDecisionEngine.CheckCapacity(
            metricsAvailable: snapshot.Available,
            currentMemoryUsedBytes: snapshot.Node.MemUsedBytes,
            memoryCapacityBytes: snapshot.Node.MemCapacityBytes,
            additionalMemoryRequestBytes: additionalBytes);

        if (result == AutoScaleDecisionEngine.CapacityCheckResult.Warn)
        {
            _logger.LogWarning(
                "Capacity warning: {Action} would project memory usage above capacity " +
                "(current {UsedBytes} + requested {AdditionalBytes} > capacity {CapacityBytes}).",
                action, snapshot.Node.MemUsedBytes, additionalBytes, snapshot.Node.MemCapacityBytes);
        }
    }

    // --- label parsing + mapping ---

    private static IReadOnlyDictionary<string, string> ConfigOf(IDictionary<string, string>? labels) =>
        Deserialize<Dictionary<string, string>>(labels, ContainerLabels.Config)
            ?? new Dictionary<string, string>();

    private static IReadOnlyList<PortMapping> PortsOf(IDictionary<string, string>? labels) =>
        Deserialize<List<PortMapping>>(labels, ContainerLabels.Ports)
            ?? (IReadOnlyList<PortMapping>)Array.Empty<PortMapping>();

    private static ResourceSpec? ResourcesOf(IDictionary<string, string>? labels) =>
        Deserialize<ResourceSpec>(labels, ContainerLabels.Resources);

    private static IReadOnlyDictionary<string, string> SecretKeyRefsOf(IDictionary<string, string>? labels) =>
        Deserialize<Dictionary<string, string>>(labels, ContainerLabels.SecretKeys)
            ?? new Dictionary<string, string>();

    private static T? Deserialize<T>(IDictionary<string, string>? labels, string key) where T : class
    {
        if (labels is null || !labels.TryGetValue(key, out var json) || string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTime CreatedAtOf(IDictionary<string, string>? labels, DateTime fallback)
    {
        // The original deploy time survives config-change recreations via the
        // created-at label; the container's own Created resets on recreate.
        if (labels is not null &&
            labels.TryGetValue(ContainerLabels.CreatedAt, out var raw) &&
            DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed.ToUniversalTime();
        }
        return fallback;
    }

    private async Task<ServerSummary> MapToSummaryAsync(ContainerListResponse container, CancellationToken ct)
    {
        var name = container.Names?.FirstOrDefault()?.TrimStart('/')
            ?? container.ID[..12];
        var labels = container.Labels;
        var ports = PortsOf(labels);

        var ready = await _prober.IsReadyAsync(container.ID, container.State, ports, ct);

        return new ServerSummary(
            Name: name,
            Game: labels?.TryGetValue(ContainerLabels.Game, out var game) == true ? game : name,
            Image: container.Image,
            Status: ContainerStatusMapper.Map(
                container.State, ready, _deployTracker.Get(name) is { Failed: false }),
            Replicas: ContainerStatusMapper.Replicas(container.State),
            CreatedAt: CreatedAtOf(labels, container.Created));
    }

    private async Task<ServerDetail> MapToDetailAsync(ContainerInspectResponse container, CancellationToken ct)
    {
        var name = container.Name.TrimStart('/');
        var labels = container.Config?.Labels;
        var ports = PortsOf(labels);
        var state = container.State?.Status;

        var ready = await _prober.IsReadyAsync(container.ID, state, ports, ct);

        return new ServerDetail(
            Name: name,
            Game: labels?.TryGetValue(ContainerLabels.Game, out var game) == true ? game : name,
            Image: container.Config?.Image ?? "unknown",
            Status: ContainerStatusMapper.Map(
                state, ready, _deployTracker.Get(name) is { Failed: false }),
            Replicas: ContainerStatusMapper.Replicas(state),
            CreatedAt: CreatedAtOf(labels, container.Created),
            Ports: ports,
            Resources: ResourcesOf(labels),
            Players: null);
    }
}
