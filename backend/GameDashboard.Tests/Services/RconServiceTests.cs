using System.Reflection;
using GameDashboard.Api.Exceptions;
using GameDashboard.Api.Models;
using GameDashboard.Api.Services;
using GameDashboard.Api.Services.Docker;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using IDockerClient = Docker.DotNet.IDockerClient;

namespace GameDashboard.Tests.Services;

/// <summary>
/// Covers RconService's fail-soft contract (Req 10.3) and its response-parsing
/// logic in isolation. The actual TCP/RCON handshake requires a live game server
/// and is exercised in the live verification pass.
/// </summary>
public class RconServiceTests
{
    private static RconService CreateWithUnreachableEngine()
    {
        var factory = new Mock<IDockerClientFactory>();
        factory.Setup(f => f.TryGetClientAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IDockerClient?)null, "engine unreachable"));

        return new RconService(
            factory.Object, Mock.Of<ISecretsStore>(), NullLogger<RconService>.Instance);
    }

    [Fact]
    public async Task QueryPlayerInfoAsync_Returns_Null_When_Engine_Unreachable()
    {
        var service = CreateWithUnreachableEngine();

        var result = await service.QueryPlayerInfoAsync("cs2-server", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task QueryPlayerInfoAsync_Never_Throws_When_Engine_Unreachable()
    {
        var service = CreateWithUnreachableEngine();

        var exception = await Record.ExceptionAsync(
            () => service.QueryPlayerInfoAsync("cs2-server", CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task SendCommandAsync_Throws_RconUnavailable_When_Engine_Unreachable()
    {
        var service = CreateWithUnreachableEngine();

        await Assert.ThrowsAsync<RconUnavailableException>(
            () => service.SendCommandAsync("cs2-server", "status", CancellationToken.None));
    }

    // --- response parsing (via reflection, since the parsers are private static) ---

    [Theory]
    [InlineData(
        "hostname: My Server\nplayers     : 3 humans, 0 bots (16 max)\nmap         : de_dust2\n",
        3, 16, "de_dust2")]
    [InlineData(
        "players     : 0 humans, 2 bots (10 max)\nmap         : de_mirage\n",
        0, 10, "de_mirage")]
    public void ParseSourceStatus_Extracts_Players_And_Map(string response, int expectedCurrent, int expectedMax, string expectedMap)
    {
        var result = InvokeParse("ParseSourceStatus", response);

        Assert.NotNull(result);
        Assert.Equal(expectedCurrent, result!.CurrentPlayers);
        Assert.Equal(expectedMax, result.MaxPlayers);
        Assert.Equal(expectedMap, result.CurrentMap);
    }

    [Fact]
    public void ParseSourceStatus_Returns_Null_When_Players_Line_Missing()
    {
        var result = InvokeParse("ParseSourceStatus", "hostname: My Server\nmap: de_dust2\n");

        Assert.Null(result);
    }

    [Theory]
    [InlineData("There are 2 of a max of 10 players online: Alice, Bob", 2, 10)]
    [InlineData("There are 0 of a maximum of 20 players online:", 0, 20)]
    public void ParseMinecraftList_Extracts_Players(string response, int expectedCurrent, int expectedMax)
    {
        var result = InvokeParse("ParseMinecraftList", response);

        Assert.NotNull(result);
        Assert.Equal(expectedCurrent, result!.CurrentPlayers);
        Assert.Equal(expectedMax, result.MaxPlayers);
        Assert.Null(result.CurrentMap);
    }

    [Fact]
    public void ParseMinecraftList_Returns_Null_For_Unrecognized_Response()
    {
        var result = InvokeParse("ParseMinecraftList", "Unknown command");

        Assert.Null(result);
    }

    private static PlayerInfo? InvokeParse(string methodName, string response)
    {
        var method = typeof(RconService).GetMethod(
            methodName, BindingFlags.NonPublic | BindingFlags.Static)!;

        return (PlayerInfo?)method.Invoke(null, new object[] { response });
    }
}
