using GameDashboard.Api.Configuration;
using GameDashboard.Api.Exceptions;
using GameDashboard.Api.GameTemplates;
using GameDashboard.Api.Models;
using GameDashboard.Api.Services;
using k8s;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace GameDashboard.Tests.Services;

/// <summary>
/// Covers the validation/guard logic of the Phase 4 write operations that can be
/// exercised without a live cluster. Full happy-path deploy/scale/delete is verified
/// live against Docker Desktop (Task 4 verification) and in the Phase 10 integration
/// suite.
/// </summary>
public class KubernetesServiceWriteTests
{
    private const string Namespace = "game-servers";

    private static KubernetesService CreateWithReachableCluster(out Mock<k8s.IKubernetes> client)
    {
        client = new Mock<k8s.IKubernetes>();
        var factory = new Mock<IKubernetesClientFactory>();
        var captured = client.Object;
        IKubernetes? outClient = captured;
        string? outErr = null;
        factory.Setup(f => f.TryGetClient(out outClient, out outErr)).Returns(true);

        var options = Options.Create(new DashboardOptions { Namespace = Namespace });
        var metricsService = new Mock<IMetricsService>();
        metricsService.Setup(m => m.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetricsSnapshot(false, null, Array.Empty<PodMetricsInfo>(), "unavailable"));

        return new KubernetesService(
            factory.Object, new DeploymentBuilderService(), Mock.Of<IRconService>(), metricsService.Object, options, NullLogger<KubernetesService>.Instance);
    }

    private static KubernetesService CreateWithUnreachableCluster()
    {
        var factory = new Mock<IKubernetesClientFactory>();
        IKubernetes? nullClient = null;
        string? err = "cluster unreachable";
        factory.Setup(f => f.TryGetClient(out nullClient, out err)).Returns(false);

        var options = Options.Create(new DashboardOptions { Namespace = Namespace });
        var metricsService = new Mock<IMetricsService>();
        metricsService.Setup(m => m.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetricsSnapshot(false, null, Array.Empty<PodMetricsInfo>(), "unavailable"));

        return new KubernetesService(
            factory.Object, new DeploymentBuilderService(), Mock.Of<IRconService>(), metricsService.Object, options, NullLogger<KubernetesService>.Instance);
    }

    [Fact]
    public async Task DeployServerAsync_Rejects_Unknown_Game()
    {
        var service = CreateWithReachableCluster(out _);
        var request = new DeployServerRequest("my-server", "some/unknown-image:latest", null, null);

        await Assert.ThrowsAsync<UnknownGameException>(
            () => service.DeployServerAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task DeployServerAsync_Rejects_Invalid_Server_Name_Before_Touching_Cluster()
    {
        var service = CreateWithReachableCluster(out _);
        var request = new DeployServerRequest("Invalid_Name", CuratedGameTemplates.Cs2.ImageTag, null, null);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.DeployServerAsync(request, CancellationToken.None));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(5)]
    public async Task ScaleServerAsync_Rejects_Replica_Counts_Outside_0_And_1(int replicas)
    {
        var service = CreateWithReachableCluster(out _);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.ScaleServerAsync("my-server", replicas, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ScaleServerAsync_Accepts_0_And_1_As_Valid(int replicas)
    {
        // Reject happens before any cluster call, so a valid value must get past the
        // guard. With an unreachable cluster it then surfaces as ClusterUnreachable,
        // proving the value itself was accepted (not an ArgumentException).
        var service = CreateWithUnreachableCluster();

        await Assert.ThrowsAsync<ClusterUnreachableException>(
            () => service.ScaleServerAsync("my-server", replicas, CancellationToken.None));
    }

    [Fact]
    public async Task DeployServerAsync_Throws_ClusterUnreachable_When_Cluster_Down()
    {
        var service = CreateWithUnreachableCluster();
        var request = new DeployServerRequest("my-server", CuratedGameTemplates.Cs2.ImageTag, null, null);

        await Assert.ThrowsAsync<ClusterUnreachableException>(
            () => service.DeployServerAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteServerAsync_Throws_ClusterUnreachable_When_Cluster_Down()
    {
        var service = CreateWithUnreachableCluster();

        await Assert.ThrowsAsync<ClusterUnreachableException>(
            () => service.DeleteServerAsync("my-server", deleteData: true, CancellationToken.None));
    }

    [Fact]
    public async Task GetConfigAsync_Throws_ClusterUnreachable_When_Cluster_Down()
    {
        var service = CreateWithUnreachableCluster();

        await Assert.ThrowsAsync<ClusterUnreachableException>(
            () => service.GetConfigAsync("my-server", CancellationToken.None));
    }

    [Fact]
    public async Task DeployServerAsync_Validates_Name_Even_When_Cluster_Down()
    {
        // Name validation must run before the cluster is consulted (fail fast, Req 14.5).
        var service = CreateWithUnreachableCluster();
        var request = new DeployServerRequest("BAD NAME", CuratedGameTemplates.Cs2.ImageTag, null, null);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.DeployServerAsync(request, CancellationToken.None));
    }
}
