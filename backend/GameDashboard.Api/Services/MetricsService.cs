using GameDashboard.Api.Configuration;
using k8s;
using k8s.Autorest;
using Microsoft.Extensions.Options;
using GameDashboard.Api.Models;

namespace GameDashboard.Api.Services;

/// <summary>
/// See <see cref="IMetricsService"/>.
/// </summary>
public sealed class MetricsService : IMetricsService
{
    private readonly IKubernetesClientFactory _clientFactory;
    private readonly DashboardOptions _options;
    private readonly ILogger<MetricsService> _logger;

    private volatile bool _metricsAvailable;

    public MetricsService(
        IKubernetesClientFactory clientFactory,
        IOptions<DashboardOptions> options,
        ILogger<MetricsService> logger)
    {
        _clientFactory = clientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public bool MetricsAvailable => _metricsAvailable;

    public async Task<MetricsSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        if (!_clientFactory.TryGetClient(out var client, out var clientError))
        {
            _metricsAvailable = false;
            return Unavailable($"Cluster unreachable: {clientError}");
        }

        // Node capacity comes from the Node object itself and is always available
        // regardless of whether metrics-server is installed.
        double cpuCapacityMillicores = 0;
        long memCapacityBytes = 0;
        try
        {
            var nodes = await client!.CoreV1.ListNodeAsync(cancellationToken: ct);
            var node = nodes.Items.FirstOrDefault();
            if (node?.Status?.Capacity is { } capacity)
            {
                cpuCapacityMillicores = capacity.TryGetValue("cpu", out var cpu) ? ParseCpuMillicores(cpu.ToString()) : 0;
                memCapacityBytes = capacity.TryGetValue("memory", out var mem) ? ParseMemoryBytes(mem.ToString()) : 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read node capacity.");
        }

        // Usage requires the metrics-server aggregated API and is the part that is
        // absent by default on Docker Desktop (Req 9.3).
        try
        {
            k8s.Models.NodeMetricsList nodeMetricsList = await client!.GetKubernetesNodesMetricsAsync();
            var podMetricsList = await client.GetKubernetesPodsMetricsByNamespaceAsync(_options.Namespace);

            _metricsAvailable = true;

            double cpuUsedMillicores = 0;
            long memUsedBytes = 0;
            foreach (k8s.Models.NodeMetrics nodeMetrics in nodeMetricsList.Items)
            {
                cpuUsedMillicores += ParseCpuMillicores(nodeMetrics.Usage["cpu"].ToString());
                memUsedBytes += ParseMemoryBytes(nodeMetrics.Usage["memory"].ToString());
            }

            var pods = new List<PodMetricsInfo>();
            foreach (var podMetrics in podMetricsList.Items)
            {
                var podCpu = podMetrics.Containers.Sum(c => ParseCpuMillicores(c.Usage["cpu"].ToString()));
                var podMem = podMetrics.Containers.Sum(c => ParseMemoryBytes(c.Usage["memory"].ToString()));
                pods.Add(new PodMetricsInfo(podMetrics.Metadata.Name, podCpu, podMem));
            }

            var node = new Models.NodeMetrics(cpuUsedMillicores, cpuCapacityMillicores, memUsedBytes, memCapacityBytes);
            return new MetricsSnapshot(Available: true, node, pods, UnavailableReason: null);
        }
        catch (HttpOperationException ex) when (
            ex.Response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.ServiceUnavailable)
        {
            // The canonical "metrics-server is not installed / not ready" response:
            // the metrics.k8s.io aggregated API group simply isn't registered.
            _metricsAvailable = false;
            _logger.LogInformation("metrics-server is not available in this cluster: {Status}", ex.Response.StatusCode);
            return Unavailable("metrics-server is not installed in this cluster.");
        }
        catch (Exception ex)
        {
            _metricsAvailable = false;
            _logger.LogWarning(ex, "Unexpected error querying metrics-server.");
            return Unavailable($"Unexpected error querying metrics: {ex.Message}");
        }
    }

    private static MetricsSnapshot Unavailable(string reason) =>
        new(Available: false, Node: null, Pods: Array.Empty<PodMetricsInfo>(), UnavailableReason: reason);

    private static double ParseCpuMillicores(string value) =>
        value.EndsWith('n') ? double.Parse(value[..^1]) / 1_000_000
        : value.EndsWith('m') ? double.Parse(value[..^1])
        : double.Parse(value) * 1000;

    private static long ParseMemoryBytes(string value)
    {
        if (value.EndsWith("Ki")) return (long)(double.Parse(value[..^2]) * 1024);
        if (value.EndsWith("Mi")) return (long)(double.Parse(value[..^2]) * 1024 * 1024);
        if (value.EndsWith("Gi")) return (long)(double.Parse(value[..^2]) * 1024 * 1024 * 1024);
        return long.Parse(value);
    }
}
