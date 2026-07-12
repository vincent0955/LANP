using System.Net;
using System.Net.Http.Json;
using GameDashboard.Api.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace GameDashboard.Tests.Integration;

/// <summary>
/// Contract test for GET /api/network, which backs the frontend's copyable
/// "ip:port" join addresses. The actual address list depends on the machine's
/// adapters, so this asserts the shape: 200, a non-null list, and every entry a
/// parseable non-loopback IPv4 (the point of the endpoint is a LAN-shareable
/// address, never 127.0.0.1).
/// </summary>
public class NetworkEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public NetworkEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Network_Endpoint_Returns_Shareable_Ipv4_Addresses()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/network");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var info = await response.Content.ReadFromJsonAsync<NetworkInfo>();
        Assert.NotNull(info);
        Assert.NotNull(info.LanAddresses);
        foreach (var address in info.LanAddresses)
        {
            Assert.True(IPAddress.TryParse(address, out var parsed), $"not an IP: {address}");
            Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, parsed!.AddressFamily);
            Assert.False(IPAddress.IsLoopback(parsed), $"loopback leaked: {address}");
        }

        // Public address is best-effort (external lookup): null is acceptable, but
        // a non-null value must be a non-loopback IPv4.
        if (info.PublicAddress is not null)
        {
            Assert.True(IPAddress.TryParse(info.PublicAddress, out var publicIp),
                $"not an IP: {info.PublicAddress}");
            Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, publicIp!.AddressFamily);
            Assert.False(IPAddress.IsLoopback(publicIp), $"loopback leaked: {info.PublicAddress}");
        }

        // The forwardable window backs the "forward these ports once" UI copy; it
        // must be a sane, non-empty range inside Kubernetes' NodePort space.
        Assert.Equal(GameDashboard.Api.Services.DeploymentBuilderService.NodePortRangeStart, info.NodePortRangeStart);
        Assert.Equal(GameDashboard.Api.Services.DeploymentBuilderService.NodePortRangeEnd, info.NodePortRangeEnd);
        Assert.InRange(info.NodePortRangeStart, 30000, 32767);
        Assert.InRange(info.NodePortRangeEnd, info.NodePortRangeStart, 32767);
    }
}
