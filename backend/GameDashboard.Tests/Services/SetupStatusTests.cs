using Docker.DotNet;
using GameDashboard.Api.Configuration;
using GameDashboard.Api.Models;
using GameDashboard.Api.Services;
using GameDashboard.Api.Services.Docker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace GameDashboard.Tests.Services;

/// <summary>
/// Covers GetSetupStatusAsync's detection logic (Req 13.2). Secrets are no
/// longer part of setup — each server manages its own on its detail page, so
/// the readiness snapshot only reports engine reachability and metrics now
/// (per-server secret behaviour is covered in DockerServiceTests).
/// </summary>
public class SetupStatusTests
{
    private static DockerService Create(bool engineReachable)
    {
        var factory = new Mock<IDockerClientFactory>();
        if (engineReachable)
        {
            var client = new Mock<IDockerClient>();
            client.SetupGet(c => c.System).Returns(Mock.Of<ISystemOperations>());
            factory.Setup(f => f.TryGetClientAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync((client.Object, null));
        }
        else
        {
            factory.Setup(f => f.TryGetClientAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(((IDockerClient?)null, "engine unreachable"));
        }

        var metrics = new Mock<IMetricsService>();
        metrics.Setup(m => m.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetricsSnapshot(false, null, Array.Empty<PodMetricsInfo>(), "unavailable"));

        return new DockerService(
            factory.Object,
            new ContainerSpecBuilder(),
            new DeployTracker(),
            Mock.Of<ISecretsStore>(),
            Mock.Of<ILastActiveStore>(),
            Mock.Of<ITcpReadinessProber>(),
            Mock.Of<IRconService>(),
            metrics.Object,
            Mock.Of<IMinecraftMetadataService>(),
            Options.Create(new DashboardOptions()),
            NullLogger<DockerService>.Instance);
    }

    [Fact]
    public async Task GetSetupStatusAsync_Reports_Engine_Unreachable_With_A_Warning()
    {
        var service = Create(engineReachable: false);

        var status = await service.GetSetupStatusAsync(CancellationToken.None);

        Assert.False(status.DockerEngineReachable);
        // docker stats is built into the engine: metrics are available exactly
        // when the engine is (the k8s metrics-server middle state is gone).
        Assert.False(status.MetricsAvailable);
        Assert.NotEmpty(status.Warnings);
    }

    [Fact]
    public async Task GetSetupStatusAsync_Never_Throws_When_Engine_Unreachable()
    {
        var service = Create(engineReachable: false);

        var exception = await Record.ExceptionAsync(() => service.GetSetupStatusAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task GetSetupStatusAsync_Reports_All_Green_When_Engine_Up()
    {
        var service = Create(engineReachable: true);

        var status = await service.GetSetupStatusAsync(CancellationToken.None);

        Assert.True(status.DockerEngineReachable);
        Assert.True(status.MetricsAvailable);
        // Nothing else is required for readiness, so a reachable engine means no warnings.
        Assert.Empty(status.Warnings);
    }
}
