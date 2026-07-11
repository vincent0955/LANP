using GameDashboard.Api.Configuration;
using GameDashboard.Api.Exceptions;
using GameDashboard.Api.Models;
using GameDashboard.Api.Services;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace GameDashboard.Tests.Services;

public class KubernetesServiceTests
{
    private const string Namespace = "game-servers";

    private static (KubernetesService Service, Mock<IKubernetes> Client) CreateService()
    {
        var client = new Mock<IKubernetes>();
        var factory = new Mock<IKubernetesClientFactory>();

        IKubernetes? outClient = client.Object;
        factory.Setup(f => f.TryGetClient(out outClient, out It.Ref<string?>.IsAny))
            .Returns(new TryGetClientCallback((out IKubernetes? c, out string? e) =>
            {
                c = client.Object;
                e = null;
                return true;
            }));

        var options = Options.Create(new DashboardOptions { Namespace = Namespace });
        var metricsService = new Mock<IMetricsService>();
        metricsService.Setup(m => m.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetricsSnapshot(false, null, Array.Empty<PodMetricsInfo>(), "unavailable"));

        var service = new KubernetesService(factory.Object, new DeploymentBuilderService(), Mock.Of<IRconService>(), metricsService.Object, options, NullLogger<KubernetesService>.Instance);
        return (service, client);
    }

    private delegate bool TryGetClientCallback(out IKubernetes? client, out string? error);

    [Fact]
    public async Task GetHealthAsync_Reports_Unreachable_When_Factory_Fails()
    {
        var factory = new Mock<IKubernetesClientFactory>();
        IKubernetes? nullClient = null;
        string? err = "kubeconfig not found";
        factory.Setup(f => f.TryGetClient(out nullClient, out err)).Returns(false);

        var options = Options.Create(new DashboardOptions { Namespace = Namespace });
        var metricsService = new Mock<IMetricsService>();
        metricsService.Setup(m => m.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetricsSnapshot(false, null, Array.Empty<PodMetricsInfo>(), "unavailable"));

        var service = new KubernetesService(factory.Object, new DeploymentBuilderService(), Mock.Of<IRconService>(), metricsService.Object, options, NullLogger<KubernetesService>.Instance);

        var health = await service.GetHealthAsync(CancellationToken.None);

        Assert.False(health.ClusterReachable);
        Assert.False(health.NamespaceReady);
        Assert.Equal(Namespace, health.Namespace);
        Assert.NotNull(health.Error);
    }

    [Fact]
    public async Task ListServersAsync_Returns_Empty_List_When_No_Deployments()
    {
        var (service, client) = CreateServiceWithEmptyDeployments();

        var result = await service.ListServersAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData(0, "Running", true, ServerStatus.Stopped)]
    [InlineData(1, "Running", true, ServerStatus.Running)]
    // Running-but-not-ready is a server still starting up (readiness probe not
    // passing yet — e.g. first-deploy download), not an error. Failure states
    // (CrashLoopBackOff etc.) are covered in PodWatchServiceStatusMappingTests
    // against the shared ServerStatusMapper.
    [InlineData(1, "Running", false, ServerStatus.Pending)]
    [InlineData(1, "Pending", true, ServerStatus.Pending)]
    [InlineData(1, "Failed", true, ServerStatus.Error)]
    public void MapStatus_Follows_Design_Rules(int replicas, string phase, bool containersReady, ServerStatus expected)
    {
        var pod = new V1Pod
        {
            Status = new V1PodStatus
            {
                Phase = phase,
                ContainerStatuses = new List<V1ContainerStatus>
                {
                    new() { Ready = containersReady }
                }
            }
        };

        var pods = replicas == 0 ? Array.Empty<V1Pod>() : new[] { pod };

        var result = InvokeMapStatus(replicas, pods);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void MapStatus_Returns_Pending_When_Replicas_Positive_But_No_Pod_Found()
    {
        var result = InvokeMapStatus(1, Array.Empty<V1Pod>());
        Assert.Equal(ServerStatus.Pending, result);
    }

    [Fact]
    public async Task GetServerAsync_Returns_Null_When_Deployment_Not_Found()
    {
        var client = new Mock<IKubernetes>();
        var appsV1 = new Mock<IAppsV1Operations>();

        appsV1.Setup(a => a.ReadNamespacedDeploymentWithHttpMessagesAsync(
                "missing-server", Namespace, It.IsAny<bool?>(),
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpOperationException
            {
                Response = new HttpResponseMessageWrapper(
                    new HttpResponseMessage(System.Net.HttpStatusCode.NotFound), string.Empty)
            });

        client.SetupGet(c => c.AppsV1).Returns(appsV1.Object);

        var factory = new Mock<IKubernetesClientFactory>();
        IKubernetes? outClient = client.Object;
        string? outErr = null;
        factory.Setup(f => f.TryGetClient(out outClient, out outErr)).Returns(true);

        var options = Options.Create(new DashboardOptions { Namespace = Namespace });
        var metricsService = new Mock<IMetricsService>();
        metricsService.Setup(m => m.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetricsSnapshot(false, null, Array.Empty<PodMetricsInfo>(), "unavailable"));

        var service = new KubernetesService(factory.Object, new DeploymentBuilderService(), Mock.Of<IRconService>(), metricsService.Object, options, NullLogger<KubernetesService>.Instance);

        var result = await service.GetServerAsync("missing-server", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ListServersAsync_Throws_ClusterUnreachableException_When_Client_Unavailable()
    {
        var factory = new Mock<IKubernetesClientFactory>();
        IKubernetes? nullClient = null;
        string? err = "cluster unreachable";
        factory.Setup(f => f.TryGetClient(out nullClient, out err)).Returns(false);

        var options = Options.Create(new DashboardOptions { Namespace = Namespace });
        var metricsService = new Mock<IMetricsService>();
        metricsService.Setup(m => m.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetricsSnapshot(false, null, Array.Empty<PodMetricsInfo>(), "unavailable"));

        var service = new KubernetesService(factory.Object, new DeploymentBuilderService(), Mock.Of<IRconService>(), metricsService.Object, options, NullLogger<KubernetesService>.Instance);

        await Assert.ThrowsAsync<ClusterUnreachableException>(
            () => service.ListServersAsync(CancellationToken.None));
    }

    // --- helpers ---

    private static (KubernetesService, Mock<IKubernetes>) CreateServiceWithEmptyDeployments()
    {
        var client = new Mock<IKubernetes>();
        var appsV1 = new Mock<IAppsV1Operations>();

        appsV1.Setup(a => a.ListNamespacedDeploymentWithHttpMessagesAsync(
                Namespace,
                It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(),
                It.IsAny<int?>(), It.IsAny<bool?>(), It.IsAny<bool?>(),
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HttpOperationResponse<V1DeploymentList>
            {
                Body = new V1DeploymentList { Items = new List<V1Deployment>() }
            });

        client.SetupGet(c => c.AppsV1).Returns(appsV1.Object);

        var factory = new Mock<IKubernetesClientFactory>();
        IKubernetes? outClient = client.Object;
        string? outErr = null;
        factory.Setup(f => f.TryGetClient(out outClient, out outErr)).Returns(true);

        var options = Options.Create(new DashboardOptions { Namespace = Namespace });
        var metricsService = new Mock<IMetricsService>();
        metricsService.Setup(m => m.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetricsSnapshot(false, null, Array.Empty<PodMetricsInfo>(), "unavailable"));

        var service = new KubernetesService(factory.Object, new DeploymentBuilderService(), Mock.Of<IRconService>(), metricsService.Object, options, NullLogger<KubernetesService>.Instance);

        return (service, client);
    }

    /// <summary>
    /// MapStatus is private; exercised via reflection so the status-derivation rules
    /// (the core business logic of this service) get direct unit test coverage without
    /// requiring a full mocked round-trip through the Kubernetes client for every case.
    /// </summary>
    private static ServerStatus InvokeMapStatus(int replicas, IReadOnlyList<V1Pod> pods)
    {
        var method = typeof(KubernetesService).GetMethod(
            "MapStatus",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        return (ServerStatus)method.Invoke(null, new object[] { replicas, pods })!;
    }
}
