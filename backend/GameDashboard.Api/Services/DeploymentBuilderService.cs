using GameDashboard.Api.GameTemplates;
using GameDashboard.Api.Models;
using k8s.Models;

namespace GameDashboard.Api.Services;

/// <summary>
/// See <see cref="IDeploymentBuilder"/>. Pure logic, no cluster I/O.
/// </summary>
public sealed class DeploymentBuilderService : IDeploymentBuilder
{
    private const string AppLabel = "app";

    /// <summary>
    /// The dashboard allocates NodePorts from this deliberately small window
    /// (50 ports) instead of the full Kubernetes range [30000, 32767], so a user
    /// can forward 30000–30049 (TCP + UDP) on their router once and every server
    /// they ever deploy is reachable from the internet — no per-server router
    /// trips. Exposed via GET /api/network so the frontend copy stays in sync.
    /// At 2–3 ports per server this still allows ~20 concurrent servers.
    /// </summary>
    public const int NodePortRangeStart = 30000;
    public const int NodePortRangeEnd = 30049;

    public ServerManifestSet Build(
        GameTemplate template,
        DeployServerRequest request,
        IReadOnlySet<int> usedNodePorts,
        string namespaceName,
        string secretName)
    {
        ServerNameValidator.EnsureValid(request.Name);

        var resources = request.Resources ?? template.DefaultResources;
        ValidateResources(resources);

        var assignedPorts = AssignNodePorts(template.DefaultPorts, usedNodePorts);
        var mergedConfig = MergeConfig(template, request);

        // Minecraft Java runs the itzg image variant whose JVM matches the
        // requested Minecraft version; every other template runs its own tag.
        var image = template.Kind == TemplateKind.MinecraftJava
            ? MinecraftJavaImage.Resolve(mergedConfig)
            : template.ImageTag;

        var configMap = BuildConfigMap(mergedConfig, request, namespaceName);
        var pvc = BuildPvc(template, request, namespaceName);
        var deployment = BuildDeployment(template, request, image, resources, assignedPorts, namespaceName, secretName);
        var service = BuildService(request, assignedPorts, namespaceName);

        return new ServerManifestSet(deployment, service, pvc, configMap);
    }

    /// <summary>
    /// Assigns a unique NodePort in the dashboard's forwardable window to each
    /// template port, skipping any value already present in
    /// <paramref name="usedNodePorts"/>. Deterministic:
    /// scans upward from the range start so the same inputs always produce the same
    /// assignment (useful for tests and predictable behavior across restarts).
    /// </summary>
    private static IReadOnlyList<PortMapping> AssignNodePorts(
        IReadOnlyList<TemplatePort> templatePorts,
        IReadOnlySet<int> usedNodePorts)
    {
        var assigned = new List<PortMapping>(templatePorts.Count);
        var claimed = new HashSet<int>(usedNodePorts);

        foreach (var port in templatePorts)
        {
            var nodePort = FindNextFreePort(claimed);
            claimed.Add(nodePort);
            assigned.Add(new PortMapping(port.Name, port.Protocol, port.ContainerPort, nodePort));
        }

        return assigned;
    }

    private static int FindNextFreePort(HashSet<int> claimed)
    {
        for (var candidate = NodePortRangeStart; candidate <= NodePortRangeEnd; candidate++)
        {
            if (!claimed.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"All ports in the dashboard's range {NodePortRangeStart}–{NodePortRangeEnd} are in use. " +
            "Delete a server to free some up.");
    }

    private static void ValidateResources(ResourceSpec resources)
    {
        if (ParseCpuMillicores(resources.CpuRequest) > ParseCpuMillicores(resources.CpuLimit))
        {
            throw new ArgumentException(
                $"CPU request ({resources.CpuRequest}) must not exceed CPU limit ({resources.CpuLimit}).");
        }

        if (ParseMemoryBytes(resources.MemoryRequest) > ParseMemoryBytes(resources.MemoryLimit))
        {
            throw new ArgumentException(
                $"Memory request ({resources.MemoryRequest}) must not exceed memory limit ({resources.MemoryLimit}).");
        }
    }

    private static double ParseCpuMillicores(string value) =>
        value.EndsWith('m') ? double.Parse(value[..^1]) : double.Parse(value) * 1000;

    private static long ParseMemoryBytes(string value)
    {
        if (value.EndsWith("Gi")) return (long)(double.Parse(value[..^2]) * 1024 * 1024 * 1024);
        if (value.EndsWith("Mi")) return (long)(double.Parse(value[..^2]) * 1024 * 1024);
        if (value.EndsWith("Ki")) return (long)(double.Parse(value[..^2]) * 1024);
        return long.Parse(value);
    }

    private static Dictionary<string, string> MergeConfig(GameTemplate template, DeployServerRequest request)
    {
        var data = new Dictionary<string, string>(template.DefaultConfig);

        if (request.ConfigOverrides is not null)
        {
            foreach (var (key, value) in request.ConfigOverrides)
            {
                // Never allow a plaintext override to smuggle in a value for a key
                // that is supposed to be sourced from the Secret (Req 6.4, Req 14.3).
                if (template.SecretKeyRefs.ContainsKey(key))
                {
                    continue;
                }

                data[key] = value;
            }
        }

        return data;
    }

    private static V1ConfigMap BuildConfigMap(Dictionary<string, string> mergedConfig, DeployServerRequest request, string ns)
    {
        return new V1ConfigMap
        {
            ApiVersion = "v1",
            Kind = "ConfigMap",
            Metadata = new V1ObjectMeta
            {
                Name = ConfigMapNameFor(request.Name),
                NamespaceProperty = ns
            },
            Data = mergedConfig
        };
    }

    private static V1PersistentVolumeClaim BuildPvc(GameTemplate template, DeployServerRequest request, string ns)
    {
        return new V1PersistentVolumeClaim
        {
            ApiVersion = "v1",
            Kind = "PersistentVolumeClaim",
            Metadata = new V1ObjectMeta
            {
                Name = PvcNameFor(request.Name),
                NamespaceProperty = ns
            },
            Spec = new V1PersistentVolumeClaimSpec
            {
                // Req 5.2 / design.md: game servers are single-pod; ReadWriteOnce
                // is the correct access mode and must never be widened.
                AccessModes = new List<string> { "ReadWriteOnce" },
                Resources = new V1VolumeResourceRequirements
                {
                    Requests = new Dictionary<string, ResourceQuantity>
                    {
                        ["storage"] = new ResourceQuantity(FormatBytesAsGi(template.DefaultStorageBytes))
                    }
                }
            }
        };
    }

    private static V1Deployment BuildDeployment(
        GameTemplate template,
        DeployServerRequest request,
        string image,
        ResourceSpec resources,
        IReadOnlyList<PortMapping> ports,
        string ns,
        string secretName)
    {
        var containerPorts = ports.Select(p => new V1ContainerPort
        {
            Name = p.Name,
            ContainerPort = p.ContainerPort,
            Protocol = p.Protocol
        }).ToList();

        var env = template.SecretKeyRefs.Select(kv => new V1EnvVar
        {
            Name = kv.Key,
            ValueFrom = new V1EnvVarSource
            {
                SecretKeyRef = new V1SecretKeySelector
                {
                    Name = secretName,
                    Key = kv.Value
                }
            }
        }).ToList();

        return new V1Deployment
        {
            ApiVersion = "apps/v1",
            Kind = "Deployment",
            Metadata = new V1ObjectMeta
            {
                Name = request.Name,
                NamespaceProperty = ns,
                Labels = new Dictionary<string, string> { [AppLabel] = request.Name }
            },
            Spec = new V1DeploymentSpec
            {
                // Req 9.1 / design.md: game servers keep local state on disk;
                // multiple replicas would corrupt it. Always exactly 1.
                Replicas = 1,
                // Recreate, not the default RollingUpdate: a rolling restart briefly
                // runs old and new pods against the same RWO PVC (both fit on a
                // single node), risking save/world corruption. Old pod must fully
                // stop before the new one starts.
                Strategy = new V1DeploymentStrategy { Type = "Recreate" },
                Selector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string> { [AppLabel] = request.Name }
                },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta
                    {
                        Labels = new Dictionary<string, string> { [AppLabel] = request.Name }
                    },
                    Spec = new V1PodSpec
                    {
                        Containers = new List<V1Container>
                        {
                            new()
                            {
                                Name = request.Name,
                                Image = image,
                                Ports = containerPorts,
                                EnvFrom = new List<V1EnvFromSource>
                                {
                                    new() { ConfigMapRef = new V1ConfigMapEnvSource
                                        { Name = ConfigMapNameFor(request.Name) } }
                                },
                                Env = env.Count > 0 ? env : null,
                                Resources = new V1ResourceRequirements
                                {
                                    Requests = new Dictionary<string, ResourceQuantity>
                                    {
                                        ["cpu"] = new ResourceQuantity(resources.CpuRequest),
                                        ["memory"] = new ResourceQuantity(resources.MemoryRequest)
                                    },
                                    Limits = new Dictionary<string, ResourceQuantity>
                                    {
                                        ["cpu"] = new ResourceQuantity(resources.CpuLimit),
                                        ["memory"] = new ResourceQuantity(resources.MemoryLimit)
                                    }
                                },
                                VolumeMounts = new List<V1VolumeMount>
                                {
                                    new()
                                    {
                                        Name = DataVolumeName,
                                        MountPath = template.DataMountPath
                                    }
                                },
                                ReadinessProbe = BuildReadinessProbe(ports)
                            }
                        },
                        Volumes = new List<V1Volume>
                        {
                            new()
                            {
                                Name = DataVolumeName,
                                PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource
                                {
                                    ClaimName = PvcNameFor(request.Name)
                                }
                            }
                        }
                    }
                }
            }
        };
    }

    /// <summary>
    /// TCP readiness probe on the first TCP port, so status stays Pending until the
    /// game actually listens — on a first deploy that covers the whole in-container
    /// download (see ServerStatusMapper: Running-but-not-ready maps to Pending).
    /// UDP-only templates get no probe: a TCP probe against a UDP port never
    /// succeeds and would pin the server at Pending forever; their status remains
    /// "container started == Running", as before.
    /// </summary>
    private static V1Probe? BuildReadinessProbe(IReadOnlyList<PortMapping> ports)
    {
        var tcpPort = ports.FirstOrDefault(p =>
            string.Equals(p.Protocol, "TCP", StringComparison.OrdinalIgnoreCase));
        if (tcpPort is null)
        {
            return null;
        }

        return new V1Probe
        {
            TcpSocket = new V1TCPSocketAction { Port = tcpPort.ContainerPort },
            InitialDelaySeconds = 15,
            PeriodSeconds = 10,
            FailureThreshold = 3
        };
    }

    private static V1Service BuildService(DeployServerRequest request, IReadOnlyList<PortMapping> ports, string ns)
    {
        return new V1Service
        {
            ApiVersion = "v1",
            Kind = "Service",
            Metadata = new V1ObjectMeta
            {
                Name = ServiceNameFor(request.Name),
                NamespaceProperty = ns
            },
            Spec = new V1ServiceSpec
            {
                Type = "NodePort",
                Selector = new Dictionary<string, string> { [AppLabel] = request.Name },
                Ports = ports.Select(p => new V1ServicePort
                {
                    Name = p.Name,
                    Protocol = p.Protocol,
                    Port = p.ContainerPort,
                    TargetPort = p.ContainerPort,
                    NodePort = p.NodePort
                }).ToList()
            }
        };
    }

    private const string DataVolumeName = "data";

    private static string ConfigMapNameFor(string serverName) => $"{serverName}-config";
    private static string PvcNameFor(string serverName) => $"{serverName}-data";
    private static string ServiceNameFor(string serverName) => $"{serverName}-service";

    private static string FormatBytesAsGi(long bytes)
    {
        var gi = bytes / (1024.0 * 1024 * 1024);
        // Whole-Gi values render cleanly (e.g. "30Gi"); otherwise fall back to bytes.
        return gi == Math.Floor(gi) ? $"{(long)gi}Gi" : bytes.ToString();
    }
}
