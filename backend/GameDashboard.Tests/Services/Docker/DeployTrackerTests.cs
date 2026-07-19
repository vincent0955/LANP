using GameDashboard.Api.Models;
using GameDashboard.Api.Services.Docker;

namespace GameDashboard.Tests.Services.Docker;

/// <summary>
/// Covers the in-flight deploy registry's registration semantics — the
/// concurrency contract DeployServerAsync relies on to reject duplicate names
/// while a pull is still running (docs/docker-migration.md → Deploy is
/// asynchronous).
/// </summary>
public class DeployTrackerTests
{
    private static DeployTracker.PendingDeploy Pending(string name = "my-server") =>
        new(
            Name: name,
            CatalogImageTag: "itzg/minecraft-server:latest",
            Image: "itzg/minecraft-server:java21",
            Ports: new List<PortMapping> { new("game", "TCP", 25565, 30000) },
            Resources: new ResourceSpec("1000m", "2000m", "1Gi", "2Gi"),
            Config: new Dictionary<string, string> { ["EULA"] = "TRUE" },
            CreatedAt: DateTime.UtcNow);

    [Fact]
    public void TryRegister_Accepts_A_New_Name()
    {
        var tracker = new DeployTracker();

        Assert.True(tracker.TryRegister(Pending()));
        Assert.NotNull(tracker.Get("my-server"));
    }

    [Fact]
    public void TryRegister_Rejects_A_Name_With_A_Deploy_Still_In_Flight()
    {
        var tracker = new DeployTracker();
        tracker.TryRegister(Pending());

        Assert.False(tracker.TryRegister(Pending()));
    }

    [Fact]
    public void TryRegister_Replaces_A_Failed_Deploy_So_The_User_Can_Retry()
    {
        var tracker = new DeployTracker();
        tracker.TryRegister(Pending());
        tracker.MarkFailed("my-server", "pull failed");

        Assert.True(tracker.TryRegister(Pending()));

        var entry = tracker.Get("my-server")!;
        Assert.False(entry.Failed);
        Assert.Null(entry.Error);
    }

    [Fact]
    public void Remove_Frees_The_Name_For_Reregistration()
    {
        var tracker = new DeployTracker();
        tracker.TryRegister(Pending());

        tracker.Remove("my-server");

        Assert.Null(tracker.Get("my-server"));
        Assert.True(tracker.TryRegister(Pending()));
    }

    [Fact]
    public void MarkFailed_Flips_The_Entry_To_Error_With_The_Message()
    {
        var tracker = new DeployTracker();
        tracker.TryRegister(Pending());

        tracker.MarkFailed("my-server", "no space left on device");

        var entry = tracker.Get("my-server")!;
        Assert.True(entry.Failed);
        Assert.Equal("no space left on device", entry.Error);
    }

    [Fact]
    public void MarkFailed_For_An_Unknown_Name_Is_A_NoOp()
    {
        var tracker = new DeployTracker();

        tracker.MarkFailed("never-registered", "whatever");

        Assert.Null(tracker.Get("never-registered"));
    }

    [Fact]
    public void ToSummary_Reports_Pending_While_In_Flight_And_Error_After_Failure()
    {
        var tracker = new DeployTracker();
        var deploy = Pending();

        Assert.Equal(ServerStatus.Pending, tracker.ToSummary(deploy).Status);
        Assert.Equal(ServerStatus.Error, tracker.ToSummary(deploy with { Failed = true }).Status);
    }

    [Fact]
    public void ToDetail_Carries_The_Reserved_Ports_And_Resources()
    {
        var tracker = new DeployTracker();
        var deploy = Pending();

        var detail = tracker.ToDetail(deploy);

        Assert.Equal(deploy.Ports, detail.Ports);
        Assert.Equal(deploy.Resources, detail.Resources);
        Assert.Equal(1, detail.Replicas);
        Assert.Null(detail.Players);
    }

    [Fact]
    public void All_Lists_Every_Registered_Deploy()
    {
        var tracker = new DeployTracker();
        tracker.TryRegister(Pending("server-a"));
        tracker.TryRegister(Pending("server-b"));

        Assert.Equal(2, tracker.All.Count);
    }
}
