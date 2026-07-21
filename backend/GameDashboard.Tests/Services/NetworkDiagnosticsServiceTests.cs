using GameDashboard.Api.Services;

namespace GameDashboard.Tests.Services;

/// <summary>
/// Covers the pure CGNAT/private-range detection used by the reachability probe
/// and forwarding guide (WS2).
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
}
