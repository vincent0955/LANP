using Docker.DotNet;
using Docker.DotNet.Models;
using GameDashboard.Api.Services.Docker;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GameDashboard.Tests.Services.Docker;

/// <summary>
/// Covers DockerMetricsService: the never-throws availability contract
/// (Req 9.3), capacity from the engine's system info, and the docker-stats
/// CPU/memory math that replaced metrics-server readings.
/// </summary>
public class DockerMetricsServiceTests
{
    private sealed class Harness
    {
        public Mock<IContainerOperations> Containers { get; } = new();
        public Mock<ISystemOperations> System { get; } = new();
        public DockerMetricsService Service { get; }

        public Harness(bool engineReachable)
        {
            var factory = new Mock<IDockerClientFactory>();
            if (engineReachable)
            {
                var client = new Mock<IDockerClient>();
                client.SetupGet(c => c.Containers).Returns(Containers.Object);
                client.SetupGet(c => c.System).Returns(System.Object);
                factory.Setup(f => f.TryGetClientAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync((client.Object, null));

                System.Setup(s => s.GetSystemInfoAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new SystemInfoResponse { NCPU = 8, MemTotal = 16L * 1024 * 1024 * 1024 });
                Containers.Setup(c => c.ListContainersAsync(
                        It.IsAny<ContainersListParameters>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new List<ContainerListResponse>());
            }
            else
            {
                factory.Setup(f => f.TryGetClientAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(((IDockerClient?)null, "engine unreachable"));
            }

            Service = new DockerMetricsService(factory.Object, NullLogger<DockerMetricsService>.Instance);
        }

        public void AddRunningContainer(string name, ContainerStatsResponse? stats)
        {
            Containers.Setup(c => c.ListContainersAsync(
                    It.IsAny<ContainersListParameters>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<ContainerListResponse>
                {
                    new() { ID = $"{name}-id", Names = new List<string> { $"/{name}" } },
                });

            if (stats is null)
            {
                Containers.Setup(c => c.GetContainerStatsAsync(
                        $"{name}-id", It.IsAny<ContainerStatsParameters>(),
                        It.IsAny<IProgress<ContainerStatsResponse>>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new IOException("stats read failed"));
            }
            else
            {
                Containers.Setup(c => c.GetContainerStatsAsync(
                        $"{name}-id", It.IsAny<ContainerStatsParameters>(),
                        It.IsAny<IProgress<ContainerStatsResponse>>(), It.IsAny<CancellationToken>()))
                    .Callback<string, ContainerStatsParameters, IProgress<ContainerStatsResponse>, CancellationToken>(
                        (_, _, progress, _) => progress.Report(stats))
                    .Returns(Task.CompletedTask);
            }
        }
    }

    /// <summary>
    /// One stats frame: CPU went from 0 to cpuDeltaNs of a systemDeltaNs window
    /// on a 4-core box; memory usage with some page cache (inactive_file).
    /// </summary>
    private static ContainerStatsResponse Stats(
        ulong cpuDeltaNs, ulong systemDeltaNs, ulong memUsage, ulong inactiveFile) =>
        new()
        {
            CPUStats = new CPUStats
            {
                CPUUsage = new CPUUsage { TotalUsage = cpuDeltaNs },
                SystemUsage = systemDeltaNs,
                OnlineCPUs = 4,
            },
            PreCPUStats = new CPUStats
            {
                CPUUsage = new CPUUsage { TotalUsage = 0 },
                SystemUsage = 0,
            },
            MemoryStats = new MemoryStats
            {
                Usage = memUsage,
                Stats = new Dictionary<string, ulong> { ["inactive_file"] = inactiveFile },
            },
        };

    [Fact]
    public async Task GetSnapshotAsync_Reports_Unavailable_When_Engine_Unreachable()
    {
        var harness = new Harness(engineReachable: false);

        var snapshot = await harness.Service.GetSnapshotAsync(CancellationToken.None);

        Assert.False(snapshot.Available);
        Assert.Null(snapshot.Node);
        Assert.Empty(snapshot.Pods);
        Assert.NotNull(snapshot.UnavailableReason);
    }

    [Fact]
    public async Task GetSnapshotAsync_Does_Not_Throw_When_Engine_Unreachable()
    {
        var harness = new Harness(engineReachable: false);

        // The whole contract of IMetricsService is that it never throws (Req 9.3);
        // this assertion is the point of the test, not incidental.
        var exception = await Record.ExceptionAsync(() => harness.Service.GetSnapshotAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public void MetricsAvailable_Defaults_To_False_Before_Any_Query()
    {
        var harness = new Harness(engineReachable: false);

        Assert.False(harness.Service.MetricsAvailable);
    }

    [Fact]
    public async Task MetricsAvailable_Reflects_Unreachable_Engine_After_Query()
    {
        var harness = new Harness(engineReachable: false);

        await harness.Service.GetSnapshotAsync(CancellationToken.None);

        Assert.False(harness.Service.MetricsAvailable);
    }

    [Fact]
    public async Task GetSnapshotAsync_Takes_Capacity_From_The_Engines_System_Info()
    {
        var harness = new Harness(engineReachable: true);

        var snapshot = await harness.Service.GetSnapshotAsync(CancellationToken.None);

        Assert.True(snapshot.Available);
        Assert.True(harness.Service.MetricsAvailable);
        Assert.NotNull(snapshot.Node);
        Assert.Equal(8_000, snapshot.Node!.CpuCapacityMillicores);
        Assert.Equal(16L * 1024 * 1024 * 1024, snapshot.Node.MemCapacityBytes);
        Assert.Empty(snapshot.Pods);
    }

    [Fact]
    public async Task GetSnapshotAsync_Derives_Millicores_And_Memory_From_A_Stats_Frame()
    {
        var harness = new Harness(engineReachable: true);
        // 10% of total CPU on a 4-core box → 400 millicores; 1000 bytes used of
        // which 200 are page cache → 800 bytes attributed to the game.
        harness.AddRunningContainer("cs2-a",
            Stats(cpuDeltaNs: 100, systemDeltaNs: 1000, memUsage: 1000, inactiveFile: 200));

        var snapshot = await harness.Service.GetSnapshotAsync(CancellationToken.None);

        var pod = Assert.Single(snapshot.Pods);
        Assert.Equal("cs2-a", pod.ServerName);
        Assert.Equal(400, pod.CpuUsedMillicores, precision: 5);
        Assert.Equal(800, pod.MemUsedBytes);
        Assert.Equal(400, snapshot.Node!.CpuUsedMillicores, precision: 5);
        Assert.Equal(800, snapshot.Node.MemUsedBytes);
    }

    [Fact]
    public async Task One_Broken_Containers_Stats_Do_Not_Take_Down_The_Snapshot()
    {
        var harness = new Harness(engineReachable: true);
        harness.AddRunningContainer("broken-server", stats: null);

        var snapshot = await harness.Service.GetSnapshotAsync(CancellationToken.None);

        Assert.True(snapshot.Available);
        Assert.Empty(snapshot.Pods); // skipped, not fatal
    }
}
