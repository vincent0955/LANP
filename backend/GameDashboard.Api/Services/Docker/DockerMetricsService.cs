using Docker.DotNet;
using Docker.DotNet.Models;
using GameDashboard.Api.Models;

namespace GameDashboard.Api.Services.Docker;

/// <summary>
/// See <see cref="IMetricsService"/>. Replaces the metrics-server dependency:
/// `docker stats` (one-shot) is built into the engine, so metrics are
/// available exactly when the engine is — the "metrics-server not installed"
/// degraded mode is gone.
///
/// Capacity comes from the engine's system info (the WSL VM / host the engine
/// runs in). "Used" is the sum across the dashboard's running containers —
/// i.e. what the game servers consume, which is the quantity the auto-scale
/// high-water check actually cares about.
/// </summary>
public sealed class DockerMetricsService : IMetricsService
{
    private static readonly TimeSpan StatsTimeout = TimeSpan.FromSeconds(8);

    private readonly IDockerClientFactory _clientFactory;
    private readonly ILogger<DockerMetricsService> _logger;

    private volatile bool _metricsAvailable;

    public DockerMetricsService(IDockerClientFactory clientFactory, ILogger<DockerMetricsService> logger)
    {
        _clientFactory = clientFactory;
        _logger = logger;
    }

    public bool MetricsAvailable => _metricsAvailable;

    public async Task<MetricsSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        var (client, error) = await _clientFactory.TryGetClientAsync(ct);
        if (client is null)
        {
            _metricsAvailable = false;
            return Unavailable($"Docker engine unreachable: {error}");
        }

        try
        {
            var info = await client.System.GetSystemInfoAsync(ct);
            var cpuCapacityMillicores = info.NCPU * 1000d;
            var memCapacityBytes = info.MemTotal;

            var containers = await client.Containers.ListContainersAsync(new ContainersListParameters
            {
                // Running only — stats on a stopped container would block.
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["label"] = new Dictionary<string, bool> { [ContainerLabels.ManagedFilter] = true },
                    ["status"] = new Dictionary<string, bool> { ["running"] = true },
                },
            }, ct);

            var pods = new List<PodMetricsInfo>(containers.Count);
            double cpuUsedMillicores = 0;
            long memUsedBytes = 0;

            foreach (var container in containers)
            {
                var name = container.Names?.FirstOrDefault()?.TrimStart('/') ?? container.ID[..12];
                var stats = await GetOneShotStatsAsync(client, container.ID, ct);
                if (stats is null)
                {
                    continue;
                }

                var cpu = CpuMillicoresOf(stats);
                var mem = MemUsedBytesOf(stats);
                cpuUsedMillicores += cpu;
                memUsedBytes += mem;
                pods.Add(new PodMetricsInfo(name, cpu, mem));
            }

            _metricsAvailable = true;
            var node = new NodeMetrics(cpuUsedMillicores, cpuCapacityMillicores, memUsedBytes, memCapacityBytes);
            return new MetricsSnapshot(Available: true, node, pods, UnavailableReason: null);
        }
        catch (Exception ex)
        {
            _metricsAvailable = false;
            _logger.LogWarning(ex, "Failed to gather Docker metrics.");
            return Unavailable($"Failed to gather metrics: {ex.Message}");
        }
    }

    /// <summary>
    /// One sample from the stats stream (Stream=false makes the daemon return a
    /// single frame with the previous tick in PreCPUStats, which the CPU delta
    /// math needs). Null on per-container failure — one broken container must
    /// not take down the whole snapshot.
    /// </summary>
    private async Task<ContainerStatsResponse?> GetOneShotStatsAsync(
        IDockerClient client, string containerId, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(StatsTimeout);

            // Deliberately not Progress<T>: that class posts callbacks to the
            // thread pool, so the frame could land after this method returns.
            var progress = new SyncProgress();
            await client.Containers.GetContainerStatsAsync(
                containerId,
                new ContainerStatsParameters { Stream = false },
                progress,
                cts.Token);
            return progress.Last;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stats read failed for container {Container}.", containerId);
            return null;
        }
    }

    private sealed class SyncProgress : IProgress<ContainerStatsResponse>
    {
        public ContainerStatsResponse? Last { get; private set; }

        public void Report(ContainerStatsResponse value) => Last = value;
    }

    private static double CpuMillicoresOf(ContainerStatsResponse stats)
    {
        var cpuDelta = (double)stats.CPUStats.CPUUsage.TotalUsage - stats.PreCPUStats.CPUUsage.TotalUsage;
        var systemDelta = (double)stats.CPUStats.SystemUsage - stats.PreCPUStats.SystemUsage;
        if (cpuDelta <= 0 || systemDelta <= 0)
        {
            return 0;
        }

        var cpus = stats.CPUStats.OnlineCPUs > 0
            ? stats.CPUStats.OnlineCPUs
            : (ulong)(stats.CPUStats.CPUUsage.PercpuUsage?.Count ?? 1);

        // Fraction of total machine CPU × core count × 1000 = millicores,
        // matching what metrics-server reported for pods.
        return cpuDelta / systemDelta * cpus * 1000;
    }

    private static long MemUsedBytesOf(ContainerStatsResponse stats)
    {
        var usage = (long)stats.MemoryStats.Usage;

        // Match `docker stats` / cadvisor: exclude the page cache
        // (inactive_file) so a chunky world download doesn't read as RAM.
        if (stats.MemoryStats.Stats is not null &&
            stats.MemoryStats.Stats.TryGetValue("inactive_file", out var inactiveFile) &&
            (long)inactiveFile <= usage)
        {
            usage -= (long)inactiveFile;
        }

        return usage;
    }

    private static MetricsSnapshot Unavailable(string reason) =>
        new(Available: false, Node: null, Pods: Array.Empty<PodMetricsInfo>(), UnavailableReason: reason);
}
