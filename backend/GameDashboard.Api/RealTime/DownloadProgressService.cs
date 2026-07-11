using GameDashboard.Api.Configuration;
using GameDashboard.Api.Hubs;
using GameDashboard.Api.Services;
using k8s;
using k8s.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace GameDashboard.Api.RealTime;

/// <summary>
/// Pushes <see cref="DownloadProgressMessage"/> events for recently created servers
/// while their first in-container download runs, by exec-ing `du -sk` on the data
/// mount inside the pod. Exec was chosen over kubelet volume stats deliberately:
/// Docker Desktop backs PVCs with hostPath volumes, for which the kubelet collects
/// no stats at all (verified live) — while every image in the catalog ships `du`
/// (`-sk`, not GNU-only `-sb`, so busybox userlands work too).
///
/// Scope guards keep this near-free in steady state: nothing runs unless at least
/// one deployment is younger than <see cref="FreshWindow"/> with replicas &gt; 0,
/// and each exec is capped by <see cref="ExecTimeout"/>. All failures degrade
/// silently — the frontend simply shows no byte counter.
/// </summary>
public sealed class DownloadProgressService : BackgroundService
{
    private static readonly TimeSpan PushInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FreshWindow = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan ExecTimeout = TimeSpan.FromSeconds(8);

    private readonly IKubernetesClientFactory _clientFactory;
    private readonly IHubContext<DashboardHub> _hubContext;
    private readonly DashboardOptions _options;
    private readonly ILogger<DownloadProgressService> _logger;

    public DownloadProgressService(
        IKubernetesClientFactory clientFactory,
        IHubContext<DashboardHub> hubContext,
        IOptions<DashboardOptions> options,
        ILogger<DownloadProgressService> logger)
    {
        _clientFactory = clientFactory;
        _hubContext = hubContext;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PushInterval);
        while (await WaitForNextTickSafeAsync(timer, stoppingToken))
        {
            try
            {
                await PushOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Expected when the cluster is down mid-pass; progress is a
                // best-effort enhancement, never an error.
                _logger.LogDebug(ex, "Download progress pass skipped.");
            }
        }
    }

    private static async Task<bool> WaitForNextTickSafeAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task PushOnceAsync(CancellationToken ct)
    {
        if (!_clientFactory.TryGetClient(out var client, out _))
        {
            return;
        }

        var deployments = await client!.AppsV1.ListNamespacedDeploymentAsync(
            _options.Namespace, cancellationToken: ct);
        var freshServers = deployments.Items
            .Where(d =>
                (d.Spec?.Replicas ?? 0) > 0 &&
                d.Metadata?.CreationTimestamp is { } created &&
                DateTime.UtcNow - created.ToUniversalTime() < FreshWindow)
            .Select(d => d.Metadata!.Name)
            .ToHashSet(StringComparer.Ordinal);

        if (freshServers.Count == 0)
        {
            return;
        }

        var pods = await client.CoreV1.ListNamespacedPodAsync(
            _options.Namespace, cancellationToken: ct);
        var pvcs = await client.CoreV1.ListNamespacedPersistentVolumeClaimAsync(
            _options.Namespace, cancellationToken: ct);
        var timestamp = DateTimeOffset.UtcNow;

        foreach (var serverName in freshServers)
        {
            var pod = pods.Items.FirstOrDefault(p =>
                p.Metadata?.Labels?.TryGetValue("app", out var app) == true &&
                app == serverName &&
                p.Status?.Phase == "Running");
            if (pod is null)
            {
                continue; // Still scheduling/pulling — nothing to measure yet.
            }

            var mount = ResolveDataMount(pod);
            if (mount is null)
            {
                continue;
            }

            long? bytesUsed;
            try
            {
                bytesUsed = await MeasureBytesAsync(client, pod, mount.Value.MountPath, ct);
            }
            catch (Exception ex)
            {
                // Container may have no `du`, be mid-restart, or exec may be
                // disabled — skip this server, keep the loop alive.
                _logger.LogDebug(ex, "du exec failed for {Server}.", serverName);
                continue;
            }

            if (bytesUsed is null)
            {
                continue;
            }

            var capacity = CapacityOf(pvcs, mount.Value.ClaimName);
            await _hubContext.Clients.Group("events").SendAsync(
                HubEvents.DownloadProgress,
                new DownloadProgressMessage(serverName, bytesUsed.Value, capacity, timestamp),
                ct);
        }
    }

    /// <summary>
    /// Finds the PVC-backed volume in the pod spec and the container path it is
    /// mounted at. Works for both dashboard-generated manifests (volume "data")
    /// and hand-written ones (arbitrary volume names) by following the
    /// volume → volumeMount linkage rather than assuming names.
    /// </summary>
    private static (string MountPath, string ClaimName)? ResolveDataMount(V1Pod pod)
    {
        var volume = pod.Spec?.Volumes?.FirstOrDefault(v => v.PersistentVolumeClaim is not null);
        if (volume is null)
        {
            return null;
        }

        var mount = pod.Spec?.Containers?
            .SelectMany(c => c.VolumeMounts ?? Enumerable.Empty<V1VolumeMount>())
            .FirstOrDefault(m => m.Name == volume.Name);
        return mount is null ? null : (mount.MountPath, volume.PersistentVolumeClaim.ClaimName);
    }

    private static long CapacityOf(V1PersistentVolumeClaimList pvcs, string claimName)
    {
        var pvc = pvcs.Items.FirstOrDefault(p => p.Metadata?.Name == claimName);
        var quantity = pvc?.Status?.Capacity?.TryGetValue("storage", out var q) == true ? q : null;
        try
        {
            return quantity?.ToInt64() ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static async Task<long?> MeasureBytesAsync(
        IKubernetes client, V1Pod pod, string mountPath, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ExecTimeout);

        string stdout = string.Empty;
        await client.NamespacedPodExecAsync(
            pod.Metadata.Name,
            pod.Metadata.NamespaceProperty,
            pod.Spec.Containers[0].Name,
            new[] { "du", "-sk", mountPath },
            tty: false,
            action: async (_, stdOut, _) =>
            {
                using var reader = new StreamReader(stdOut);
                stdout = await reader.ReadToEndAsync(timeoutCts.Token);
            },
            cancellationToken: timeoutCts.Token);

        // `du -sk <path>` → "12345\t/data"; KiB → bytes.
        var firstToken = stdout.TrimStart().Split('\t', ' ').FirstOrDefault();
        return long.TryParse(firstToken, out var kib) ? kib * 1024 : null;
    }
}
