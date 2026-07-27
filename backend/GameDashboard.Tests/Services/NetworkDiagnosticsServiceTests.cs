using GameDashboard.Api.Models;
using GameDashboard.Api.Services;
using GameDashboard.Api.Services.Docker;

namespace GameDashboard.Tests.Services;

/// <summary>
/// Covers the pure CGNAT/private-range detection used by the reachability probe
/// and forwarding guide (WS2), plus the whole-range probe behind Setup's
/// "open all ports once" flow.
/// </summary>
public sealed class NetworkDiagnosticsServiceTests
{
    [Theory]
    [InlineData("100.64.0.1", true)]   // CGNAT shared range low
    [InlineData("100.127.255.254", true)] // CGNAT shared range high
    [InlineData("10.0.0.5", true)]     // RFC1918 (double NAT)
    [InlineData("172.16.4.9", true)]   // RFC1918
    [InlineData("192.168.1.20", true)] // RFC1918
    public void DetectCgnat_Flags_Non_Routable_Public_Addresses(string ip, bool expected)
    {
        var (detected, detail) = NetworkDiagnosticsService.DetectCgnat(ip);

        Assert.Equal(expected, detected);
        Assert.NotNull(detail);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("100.128.0.1")] // just outside the CGNAT /10
    [InlineData("172.32.0.1")]  // just outside 172.16/12
    [InlineData("203.0.113.5")]
    public void DetectCgnat_Passes_Real_Public_Addresses(string ip)
    {
        var (detected, detail) = NetworkDiagnosticsService.DetectCgnat(ip);

        Assert.False(detected);
        Assert.Null(detail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-an-ip")]
    public void DetectCgnat_Is_Inconclusive_For_Missing_Or_Invalid_Input(string? ip)
    {
        var (detected, detail) = NetworkDiagnosticsService.DetectCgnat(ip);

        Assert.False(detected);
        Assert.Null(detail);
    }

    [Fact]
    public void SampleRangePorts_Probes_Both_Ends_And_The_Middle()
    {
        var ports = NetworkDiagnosticsService.SampleRangePorts(30000, 30049);

        Assert.Equal(new[] { 30000, 30024, 30049 }, ports);
    }

    [Theory]
    [InlineData(30000, 30000, 1)] // single-port window
    [InlineData(30000, 30001, 2)] // no distinct middle
    [InlineData(30000, 30049, 3)]
    public void SampleRangePorts_Stays_Inside_The_Window_And_Never_Repeats(int start, int end, int expected)
    {
        var ports = NetworkDiagnosticsService.SampleRangePorts(start, end);

        Assert.Equal(expected, ports.Count);
        Assert.Equal(ports.Distinct().OrderBy(p => p), ports);
        Assert.All(ports, p => Assert.InRange(p, start, end));
    }

    /// <summary>
    /// The range probe is deliberately server-independent: it reports the whole
    /// forwardable window, sampled, as TCP rows — and never claims "Closed" for a
    /// port it couldn't hold open, since that says nothing about the router.
    /// </summary>
    [Fact]
    public async Task CheckRangeReachabilityAsync_Reports_The_Sampled_Window()
    {
        var service = new NetworkDiagnosticsService(
            orchestrator: null!, // the range path never looks at a server
            new FakePublicIp("203.0.113.5"),
            new FakePortChecker(ReachabilityState.Open));

        var result = await service.CheckRangeReachabilityAsync(CancellationToken.None);

        Assert.Equal("203.0.113.5", result.PublicAddress);
        Assert.False(result.CgnatDetected);
        Assert.Equal(ContainerSpecBuilder.HostPortRangeStart, result.RangeStart);
        Assert.Equal(ContainerSpecBuilder.HostPortRangeEnd, result.RangeEnd);
        Assert.Equal(
            NetworkDiagnosticsService.SampleRangePorts(result.RangeStart, result.RangeEnd),
            result.Ports.Select(p => p.Port).OrderBy(p => p));
        Assert.All(result.Ports, p =>
        {
            Assert.Equal("TCP", p.Protocol);
            Assert.NotEqual(ReachabilityState.Closed, p.External);
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
        });
    }

    [Fact]
    public async Task CheckRangeReachabilityAsync_Reports_Cgnat_Without_Probing()
    {
        var checker = new FakePortChecker(ReachabilityState.Open);
        var service = new NetworkDiagnosticsService(null!, new FakePublicIp("100.72.0.9"), checker);

        var result = await service.CheckRangeReachabilityAsync(CancellationToken.None);

        Assert.True(result.CgnatDetected);
        Assert.NotNull(result.CgnatDetail);
        Assert.Equal(0, checker.Calls);
        Assert.All(result.Ports, p => Assert.Equal(ReachabilityState.Unverified, p.External));
    }

    private sealed class FakePublicIp(string? ip) : IPublicIpService
    {
        public Task<string?> GetPublicIpAsync(CancellationToken ct) => Task.FromResult(ip);
    }

    private sealed class FakePortChecker(ReachabilityState state) : IExternalPortChecker
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<ReachabilityState> CheckTcpAsync(string publicIp, int port, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(state);
        }
    }
}
