using Docker.DotNet;
using Docker.DotNet.Models;
using GameDashboard.Api.Hubs;
using GameDashboard.Api.Services.Docker;
using Microsoft.AspNetCore.SignalR;

namespace GameDashboard.Api.RealTime;

/// <summary>
/// Pushes <see cref="DownloadProgressMessage"/> events for recently created
/// servers while their first in-container download runs, by exec-ing `du -sk`
/// on the data mount inside the container — the same trick as the k8s era
/// (`kubectl exec du -sk`), now over the Docker exec API. Every image in the
/// catalog ships `du` (`-sk`, not GNU-only `-sb`, so busybox userlands work).
///
/// Scope guards keep this near-free in steady state: nothing runs unless at
/// least one running container is younger than <see cref="FreshWindow"/>, and
/// each exec is capped by <see cref="ExecTimeout"/>. All failures degrade
/// silently — the frontend simply shows no byte counter.
/// </summary>
public sealed class DownloadProgressService : BackgroundService
{
    private static readonly TimeSpan PushInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FreshWindow = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan ExecTimeout = TimeSpan.FromSeconds(8);

    private readonly IDockerClientFactory _clientFactory;
    private readonly IHubContext<DashboardHub> _hubContext;
    private readonly ILogger<DownloadProgressService> _logger;

    public DownloadProgressService(
        IDockerClientFactory clientFactory,
        IHubContext<DashboardHub> hubContext,
        ILogger<DownloadProgressService> logger)
    {
        _clientFactory = clientFactory;
        _hubContext = hubContext;
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
                // Expected when the engine is down mid-pass; progress is a
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
        var (client, _) = await _clientFactory.TryGetClientAsync(ct);
        if (client is null)
        {
            return;
        }

        var containers = await client.Containers.ListContainersAsync(new ContainersListParameters
        {
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool> { [ContainerLabels.ManagedFilter] = true },
                ["status"] = new Dictionary<string, bool> { ["running"] = true },
            },
        }, ct);

        var timestamp = DateTimeOffset.UtcNow;

        foreach (var container in containers)
        {
            if (!IsFresh(container))
            {
                continue;
            }

            var name = container.Names?.FirstOrDefault()?.TrimStart('/') ?? container.ID[..12];
            var mountPath = container.Labels?.TryGetValue(ContainerLabels.DataMount, out var mount) == true
                ? mount : null;
            if (mountPath is null)
            {
                continue;
            }

            long? bytesUsed;
            try
            {
                bytesUsed = await MeasureBytesAsync(client, container.ID, mountPath, ct);
            }
            catch (Exception ex)
            {
                // Container may have no `du`, be mid-restart, or exec may fail —
                // skip this server, keep the loop alive.
                _logger.LogDebug(ex, "du exec failed for {Server}.", name);
                continue;
            }

            if (bytesUsed is null)
            {
                continue;
            }

            var capacity = container.Labels?.TryGetValue(ContainerLabels.StorageBytes, out var raw) == true
                && long.TryParse(raw, out var parsed) ? parsed : 0;

            await _hubContext.Clients.Group("events").SendAsync(
                HubEvents.DownloadProgress,
                new DownloadProgressMessage(name, bytesUsed.Value, capacity, timestamp),
                ct);
        }
    }

    private static bool IsFresh(ContainerListResponse container)
    {
        // The created-at label survives config recreations; fall back to the
        // container's own Created time when absent.
        var created = container.Labels?.TryGetValue(ContainerLabels.CreatedAt, out var raw) == true &&
            DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime()
            : container.Created.ToUniversalTime();

        return DateTime.UtcNow - created < FreshWindow;
    }

    private static async Task<long?> MeasureBytesAsync(
        IDockerClient client, string containerId, string mountPath, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ExecTimeout);

        var exec = await client.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
        {
            AttachStdout = true,
            AttachStderr = true,
            Cmd = new List<string> { "du", "-sk", mountPath },
        }, timeoutCts.Token);

        using var stream = await client.Exec.StartAndAttachContainerExecAsync(exec.ID, false, timeoutCts.Token);
        var stdout = await DockerStreamText.ReadAllAsync(stream, timeoutCts.Token);

        // `du -sk <path>` → "12345\t/data"; KiB → bytes.
        var firstToken = stdout.TrimStart().Split('\t', ' ').FirstOrDefault();
        return long.TryParse(firstToken, out var kib) ? kib * 1024 : null;
    }
}
