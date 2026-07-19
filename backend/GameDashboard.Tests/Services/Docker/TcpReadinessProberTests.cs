using System.Net;
using System.Net.Sockets;
using GameDashboard.Api.Models;
using GameDashboard.Api.Services.Docker;

namespace GameDashboard.Tests.Services.Docker;

/// <summary>
/// Covers the backend-side TCP readiness probe that replaced the k8s readiness
/// probe. Real loopback sockets (a listener for the success path, a closed
/// port for failure) — the dial is the behavior under test.
/// </summary>
public class TcpReadinessProberTests
{
    private static PortMapping Tcp(int hostPort) => new("game", "TCP", 27015, hostPort);
    private static PortMapping Udp(int hostPort) => new("game", "UDP", 27015, hostPort);

    [Theory]
    [InlineData("exited")]
    [InlineData("created")]
    [InlineData("restarting")]
    [InlineData(null)]
    public async Task Not_Running_Is_Never_Ready(string? state)
    {
        var prober = new TcpReadinessProber();

        Assert.False(await prober.IsReadyAsync("c1", state, new[] { Tcp(30000) }, CancellationToken.None));
    }

    [Fact]
    public async Task Udp_Only_Servers_Are_Ready_As_Soon_As_Running()
    {
        // A TCP dial against a UDP port never succeeds and would pin the status
        // at Pending forever — these templates skip the probe entirely.
        var prober = new TcpReadinessProber();

        Assert.True(await prober.IsReadyAsync("c1", "running", new[] { Udp(30000) }, CancellationToken.None));
    }

    [Fact]
    public async Task Unbound_Tcp_Port_Skips_The_Probe()
    {
        var prober = new TcpReadinessProber();

        Assert.True(await prober.IsReadyAsync("c1", "running", new[] { Tcp(0) }, CancellationToken.None));
    }

    [Fact]
    public async Task Closed_Port_Is_Not_Ready()
    {
        var prober = new TcpReadinessProber();

        // Nothing listens on this loopback port (bind-then-close guarantees it).
        int closedPort;
        using (var reserver = new TcpListener(IPAddress.Loopback, 0))
        {
            reserver.Start();
            closedPort = ((IPEndPoint)reserver.LocalEndpoint).Port;
            reserver.Stop();
        }

        Assert.False(await prober.IsReadyAsync("c1", "running", new[] { Tcp(closedPort) }, CancellationToken.None));
    }

    [Fact]
    public async Task Listening_Port_Is_Ready_And_The_Success_Is_Cached()
    {
        var prober = new TcpReadinessProber();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            Assert.True(await prober.IsReadyAsync("c1", "running", new[] { Tcp(port) }, CancellationToken.None));
        }
        finally
        {
            listener.Stop();
        }

        // Listener is gone, but the cached success stands while still running —
        // list polls must not re-dial every server every 3 seconds.
        Assert.True(await prober.IsReadyAsync("c1", "running", new[] { Tcp(port) }, CancellationToken.None));
    }

    [Fact]
    public async Task Leaving_The_Running_State_Invalidates_The_Cache()
    {
        var prober = new TcpReadinessProber();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            Assert.True(await prober.IsReadyAsync("c1", "running", new[] { Tcp(port) }, CancellationToken.None));
        }
        finally
        {
            listener.Stop();
        }

        // Stop transition drops the cache entry…
        Assert.False(await prober.IsReadyAsync("c1", "exited", new[] { Tcp(port) }, CancellationToken.None));

        // …so a restarted container re-probes from scratch (and the listener is
        // gone now, so it is genuinely not ready).
        Assert.False(await prober.IsReadyAsync("c1", "running", new[] { Tcp(port) }, CancellationToken.None));
    }
}
