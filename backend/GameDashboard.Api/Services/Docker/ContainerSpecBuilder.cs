using System.Text.Json;
using Docker.DotNet.Models;
using GameDashboard.Api.GameTemplates;
using GameDashboard.Api.Models;

namespace GameDashboard.Api.Services.Docker;

/// <summary>
/// Builds the CreateContainerParameters (plus volume name/image) for a deploy.
/// Replaces the k8s DeploymentBuilderService: same port allocator, same
/// resource validation, same config merge — pure logic, no engine I/O.
/// Secret values are resolved by the caller (DockerService) and passed in, so
/// this stays testable without a secrets store.
/// </summary>
public interface IContainerSpecBuilder
{
    /// <param name="modpackMinecraftVersion">
    /// For Minecraft modpack deploys: the pack's Minecraft version as resolved
    /// from Modrinth by the caller, or null when unknown (picks the java21
    /// fallback image). Ignored for everything else.
    /// </param>
    ContainerServerSpec Build(
        GameTemplate template,
        DeployServerRequest request,
        IReadOnlySet<int> usedHostPorts,
        IReadOnlyDictionary<string, string> secretEnv,
        string? modpackMinecraftVersion = null);
}

/// <summary>Everything DockerService needs to pull + create + start one server.</summary>
public sealed record ContainerServerSpec(
    string Name,
    string Image,
    string VolumeName,
    CreateContainerParameters CreateParameters,
    IReadOnlyList<PortMapping> Ports,
    ResourceSpec Resources,
    IReadOnlyDictionary<string, string> Config);

public sealed class ContainerSpecBuilder : IContainerSpecBuilder
{
    /// <summary>
    /// The dashboard publishes host ports from this deliberately small window
    /// (50 ports) so a user can forward 30000–30049 (TCP + UDP) on their router
    /// once and every server they ever deploy is reachable from the internet —
    /// no per-server router trips. Exposed via GET /api/network so the frontend
    /// copy stays in sync. At 2–3 ports per server this still allows ~20
    /// concurrent servers. (Kept identical to the old NodePort window so
    /// existing router/firewall setups keep working.)
    /// </summary>
    public const int HostPortRangeStart = 30000;
    public const int HostPortRangeEnd = 30049;

    public ContainerServerSpec Build(
        GameTemplate template,
        DeployServerRequest request,
        IReadOnlySet<int> usedHostPorts,
        IReadOnlyDictionary<string, string> secretEnv,
        string? modpackMinecraftVersion = null)
    {
        ServerNameValidator.EnsureValid(request.Name);

        var resources = request.Resources ?? template.DefaultResources;
        ValidateResources(resources);

        var assignedPorts = AssignHostPorts(template.DefaultPorts, usedHostPorts);
        var mergedConfig = MergeConfig(template, request);

        // Minecraft Java runs the itzg image variant whose JVM matches the
        // requested Minecraft version; every other template runs its own tag.
        var image = template.Kind == TemplateKind.MinecraftJava
            ? MinecraftJavaImage.Resolve(mergedConfig, modpackMinecraftVersion)
            : template.ImageTag;

        var env = mergedConfig
            .Select(kv => $"{kv.Key}={kv.Value}")
            .Concat(secretEnv.Select(kv => $"{kv.Key}={kv.Value}"))
            .ToList();

        var exposedPorts = assignedPorts.ToDictionary(
            p => PortKey(p),
            _ => default(EmptyStruct));

        var portBindings = assignedPorts.ToDictionary(
            p => PortKey(p),
            p => (IList<PortBinding>)new List<PortBinding>
            {
                new() { HostPort = p.NodePort.ToString() }
            });

        var volumeName = VolumeNameFor(request.Name);

        var labels = new Dictionary<string, string>
        {
            [ContainerLabels.Managed] = ContainerLabels.ManagedValue,
            [ContainerLabels.Game] = request.Name,
            [ContainerLabels.ImageTag] = template.ImageTag,
            [ContainerLabels.Config] = JsonSerializer.Serialize(mergedConfig),
            [ContainerLabels.Ports] = JsonSerializer.Serialize(assignedPorts),
            [ContainerLabels.Resources] = JsonSerializer.Serialize(resources),
            [ContainerLabels.DataMount] = template.DataMountPath,
            [ContainerLabels.StorageBytes] = template.DefaultStorageBytes.ToString(),
            [ContainerLabels.SecretKeys] = JsonSerializer.Serialize(template.SecretKeyRefs),
            [ContainerLabels.CreatedAt] = DateTime.UtcNow.ToString("o"),
        };

        var create = new CreateContainerParameters
        {
            Name = request.Name,
            Image = image,
            Env = env,
            Labels = labels,
            ExposedPorts = exposedPorts,
            HostConfig = new HostConfig
            {
                PortBindings = portBindings,
                Mounts = new List<Mount>
                {
                    new()
                    {
                        Type = "volume",
                        Source = volumeName,
                        Target = template.DataMountPath,
                    }
                },
                // unless-stopped ⇒ "exited" can only mean the user stopped it
                // (crashes restart, showing "restarting"); see ContainerStatusMapper.
                RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
                // MemoryReservation = request (soft), Memory = limit (hard),
                // NanoCPUs = CPU limit. CPU *request* has no hard Docker
                // equivalent and is recorded in the resources label only.
                MemoryReservation = ParseMemoryBytes(resources.MemoryRequest),
                Memory = ParseMemoryBytes(resources.MemoryLimit),
                NanoCPUs = (long)(ParseCpuMillicores(resources.CpuLimit) * 1_000_000),
            },
        };

        return new ContainerServerSpec(
            request.Name, image, volumeName, create, assignedPorts, resources, mergedConfig);
    }

    private static string PortKey(PortMapping p) =>
        $"{p.ContainerPort}/{p.Protocol.ToLowerInvariant()}";

    /// <summary>
    /// Assigns a unique host port in the dashboard's forwardable window to each
    /// template port, skipping any value already present in
    /// <paramref name="usedHostPorts"/>. Deterministic: scans upward from the
    /// range start so the same inputs always produce the same assignment.
    /// </summary>
    private static IReadOnlyList<PortMapping> AssignHostPorts(
        IReadOnlyList<TemplatePort> templatePorts,
        IReadOnlySet<int> usedHostPorts)
    {
        var assigned = new List<PortMapping>(templatePorts.Count);
        var claimed = new HashSet<int>(usedHostPorts);

        foreach (var port in templatePorts)
        {
            var hostPort = FindNextFreePort(claimed);
            claimed.Add(hostPort);
            assigned.Add(new PortMapping(port.Name, port.Protocol, port.ContainerPort, hostPort));
        }

        return assigned;
    }

    private static int FindNextFreePort(HashSet<int> claimed)
    {
        for (var candidate = HostPortRangeStart; candidate <= HostPortRangeEnd; candidate++)
        {
            if (!claimed.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"All ports in the dashboard's range {HostPortRangeStart}–{HostPortRangeEnd} are in use. " +
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

    internal static long ParseMemoryBytes(string value)
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
                // Never allow a plaintext override to smuggle in a value for a
                // key that is supposed to be sourced from the secrets store.
                if (template.SecretKeyRefs.ContainsKey(key))
                {
                    continue;
                }

                data[key] = value;
            }
        }

        return data;
    }

    public static string VolumeNameFor(string serverName) => $"{serverName}-data";
}
