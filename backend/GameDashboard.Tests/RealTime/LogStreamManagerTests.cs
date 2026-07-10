using GameDashboard.Api.Configuration;
using GameDashboard.Api.Hubs;
using GameDashboard.Api.RealTime;
using GameDashboard.Api.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace GameDashboard.Tests.RealTime;

/// <summary>
/// Covers LogStreamManager's subscriber bookkeeping (group membership, start/stop
/// decisions) without touching a live Kubernetes log stream. The read loop itself
/// (RunReadLoopAsync) requires a live cluster and is exercised in the live
/// verification pass instead.
/// </summary>
public class LogStreamManagerTests
{
    private const string Namespace = "game-servers";

    private static LogStreamManager CreateManager(out Mock<IKubernetesClientFactory> factory)
    {
        factory = new Mock<IKubernetesClientFactory>();
        // Cluster unreachable so SubscribeAsync's background read loop exits quickly
        // and quietly (logged warning, no exception) — subscriber bookkeeping is
        // independent of whether the underlying stream actually started.
        k8s.IKubernetes? nullClient = null;
        string? err = "unreachable";
        factory.Setup(f => f.TryGetClient(out nullClient, out err)).Returns(false);

        var hubContext = new Mock<IHubContext<DashboardHub>>();
        var clientsMock = new Mock<IHubClients>();
        clientsMock.Setup(c => c.Client(It.IsAny<string>())).Returns(Mock.Of<ISingleClientProxy>());
        clientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(Mock.Of<IClientProxy>());
        hubContext.Setup(h => h.Clients).Returns(clientsMock.Object);

        var options = Options.Create(new DashboardOptions { Namespace = Namespace });
        return new LogStreamManager(factory.Object, hubContext.Object, options, NullLogger<LogStreamManager>.Instance);
    }

    [Fact]
    public async Task SubscribeAsync_Increments_Subscriber_Count()
    {
        var manager = CreateManager(out _);

        await manager.SubscribeAsync("cs2-server", "conn-1", CancellationToken.None);

        Assert.Equal(1, manager.SubscriberCount("cs2-server"));
    }

    [Fact]
    public async Task SubscribeAsync_Multiple_Connections_Share_One_Stream_Subscriber_Count()
    {
        var manager = CreateManager(out _);

        await manager.SubscribeAsync("cs2-server", "conn-1", CancellationToken.None);
        await manager.SubscribeAsync("cs2-server", "conn-2", CancellationToken.None);

        Assert.Equal(2, manager.SubscriberCount("cs2-server"));
    }

    [Fact]
    public async Task UnsubscribeAsync_Decrements_Subscriber_Count()
    {
        var manager = CreateManager(out _);
        await manager.SubscribeAsync("cs2-server", "conn-1", CancellationToken.None);
        await manager.SubscribeAsync("cs2-server", "conn-2", CancellationToken.None);

        await manager.UnsubscribeAsync("cs2-server", "conn-1");

        Assert.Equal(1, manager.SubscriberCount("cs2-server"));
    }

    [Fact]
    public async Task UnsubscribeAsync_Last_Subscriber_Drops_Count_To_Zero()
    {
        var manager = CreateManager(out _);
        await manager.SubscribeAsync("cs2-server", "conn-1", CancellationToken.None);

        await manager.UnsubscribeAsync("cs2-server", "conn-1");

        Assert.Equal(0, manager.SubscriberCount("cs2-server"));
    }

    [Fact]
    public void SubscriberCount_Is_Zero_For_Unknown_Server()
    {
        var manager = CreateManager(out _);

        Assert.Equal(0, manager.SubscriberCount("never-subscribed"));
    }

    [Fact]
    public async Task ServerNamesSubscribedBy_Returns_All_Servers_A_Connection_Follows()
    {
        var manager = CreateManager(out _);
        await manager.SubscribeAsync("cs2-server", "conn-1", CancellationToken.None);
        await manager.SubscribeAsync("minecraft-server", "conn-1", CancellationToken.None);
        await manager.SubscribeAsync("insurgency-server", "conn-2", CancellationToken.None);

        var subscribed = manager.ServerNamesSubscribedBy("conn-1");

        Assert.Equal(2, subscribed.Count);
        Assert.Contains("cs2-server", subscribed);
        Assert.Contains("minecraft-server", subscribed);
        Assert.DoesNotContain("insurgency-server", subscribed);
    }

    [Fact]
    public async Task UnsubscribeAsync_Removes_Server_From_ServerNamesSubscribedBy()
    {
        var manager = CreateManager(out _);
        await manager.SubscribeAsync("cs2-server", "conn-1", CancellationToken.None);

        await manager.UnsubscribeAsync("cs2-server", "conn-1");

        Assert.Empty(manager.ServerNamesSubscribedBy("conn-1"));
    }

    [Fact]
    public async Task UnsubscribeAsync_For_Server_Never_Subscribed_Is_A_NoOp()
    {
        var manager = CreateManager(out _);

        // Should not throw even though "conn-1" never subscribed to anything.
        await manager.UnsubscribeAsync("cs2-server", "conn-1");

        Assert.Equal(0, manager.SubscriberCount("cs2-server"));
    }

    [Fact]
    public async Task Independent_Servers_Track_Subscribers_Separately()
    {
        var manager = CreateManager(out _);
        await manager.SubscribeAsync("cs2-server", "conn-1", CancellationToken.None);
        await manager.SubscribeAsync("minecraft-server", "conn-1", CancellationToken.None);
        await manager.SubscribeAsync("minecraft-server", "conn-2", CancellationToken.None);

        Assert.Equal(1, manager.SubscriberCount("cs2-server"));
        Assert.Equal(2, manager.SubscriberCount("minecraft-server"));
    }
}
