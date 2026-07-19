using System.Collections.Concurrent;
using System.Net.Sockets;
using GameDashboard.Api.Models;

namespace GameDashboard.Api.Services.Docker;

/// <summary>
/// Replaces the Kubernetes TCP readiness probe: the backend dials the first
/// TCP host port of a running container to decide Running vs Pending — on a
/// first deploy that covers the whole in-container game download, exactly like
/// the old probe did. UDP-only templates are always "ready" (a TCP dial
/// against a UDP port never succeeds and would pin the server at Pending
/// forever); their status remains "container started == Running", as before.
///
/// A bare connect success is not enough: published ports are served by
/// docker-proxy, which accepts every connection immediately — even while the
/// game inside is still downloading/booting — and only then dials the
/// container, closing the accepted socket when that fails. So after
/// connecting, the probe holds the socket for a short window: an EOF or reset
/// inside it means the game isn't listening yet; bytes or silence mean a live
/// listener (game servers don't speak first, they just keep the socket open).
///
/// A success is cached per container id so list calls don't re-dial every
/// server on every poll; the cache entry is dropped whenever the container is
/// observed in any non-running state (stop/restart transitions pass through
/// one), so a restarted game re-probes from scratch.
/// </summary>
public interface ITcpReadinessProber
{
    /// <summary>
    /// True when the container's game port answers (or it has no TCP port to
    /// probe). Only meaningful for state == "running"; any other state just
    /// invalidates the cache and returns false.
    /// </summary>
    Task<bool> IsReadyAsync(
        string containerId, string? state, IReadOnlyList<PortMapping> ports, CancellationToken ct);
}

public sealed class TcpReadinessProber : ITcpReadinessProber
{
    private static readonly TimeSpan DialTimeout = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// How long a connected socket must stay open to count as a real listener.
    /// docker-proxy's accept-then-close on an unreachable container lands well
    /// under this (a loopback dial into the VM fails in single-digit ms).
    /// </summary>
    private static readonly TimeSpan ProxyCloseWindow = TimeSpan.FromMilliseconds(250);

    private readonly ConcurrentDictionary<string, bool> _readyByContainer = new();

    public async Task<bool> IsReadyAsync(
        string containerId, string? state, IReadOnlyList<PortMapping> ports, CancellationToken ct)
    {
        if (state != "running")
        {
            _readyByContainer.TryRemove(containerId, out _);
            return false;
        }

        var tcpPort = ports.FirstOrDefault(p =>
            string.Equals(p.Protocol, "TCP", StringComparison.OrdinalIgnoreCase));
        if (tcpPort is null || tcpPort.NodePort == 0)
        {
            return true;
        }

        if (_readyByContainer.TryGetValue(containerId, out var cached) && cached)
        {
            return true;
        }

        var ready = await DialAsync(tcpPort.NodePort, ct);
        if (ready)
        {
            _readyByContainer[containerId] = true;
        }

        return ready;
    }

    private static async Task<bool> DialAsync(int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(DialTimeout);
            // Published ports are reachable on loopback via docker-proxy; under
            // WSL mirrored networking that relies on the loopback0 DNAT-bypass
            // rule the runtime installs (docs/docker-migration.md → networking).
            await client.ConnectAsync(System.Net.IPAddress.Loopback, port, cts.Token);

            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(ProxyCloseWindow);
            try
            {
                var read = await client.GetStream().ReadAsync(new byte[1], readCts.Token);
                return read > 0; // data → live server; EOF → docker-proxy gave up
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return true; // window elapsed with the socket held open
            }
        }
        catch
        {
            return false;
        }
    }
}
