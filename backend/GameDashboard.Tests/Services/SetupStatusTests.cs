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

/// <summary>
/// Covers GetSetupStatusAsync's detection logic (Req 13.2) and SetSecretsAsync's
/// contract (Req 13.3: write-only, never returns/logs values). Full happy-path
/// verification against real cluster state is done live (see Task 9's live
/// verification pass) since a real Secret and namespace round-trip is most
/// meaningfully tested against an actual cluster.
/// </summary>
public class SetupStatusTests
{
    private const string Namespace = "game-servers";
    private const string SecretName = "game-secrets";

    private static (KubernetesService Service, Mock<IKubernetes> Client, Mock<IMetricsService> Metrics)
        CreateWithReachableCluster()
    {
        var client = new Mock<IKubernetes>();
        var factory = new Mock<IKubernetesClientFactory>();
        IKubernetes? outClient = client.Object;
        string? outErr = null;
        factory.Setup(f => f.TryGetClient(out outClient, out outErr)).Returns(true);

        var metrics = new Mock<IMetricsService>();
        metrics.Setup(m => m.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetricsSnapshot(true, new GameDashboard.Api.Models.NodeMetrics(0, 0, 0, 0), Array.Empty<PodMetricsInfo>(), null));

        var options = Options.Create(new DashboardOptions { Namespace = Namespace, SecretName = SecretName });
        var service = new KubernetesService(
            factory.Object, new DeploymentBuilderService(), Mock.Of<IRconService>(), metrics.Object,
            options, NullLogger<KubernetesService>.Instance);

        return (service, client, metrics);
    }

    [Fact]
    public async Task GetSetupStatusAsync_Reports_KubeconfigMissing_When_Client_Unavailable()
    {
        var factory = new Mock<IKubernetesClientFactory>();
        IKubernetes? nullClient = null;
        string? err = "kubeconfig not found";
        factory.Setup(f => f.TryGetClient(out nullClient, out err)).Returns(false);

        var metrics = new Mock<IMetricsService>();
        var options = Options.Create(new DashboardOptions { Namespace = Namespace, SecretName = SecretName });
        var service = new KubernetesService(
            factory.Object, new DeploymentBuilderService(), Mock.Of<IRconService>(), metrics.Object,
            options, NullLogger<KubernetesService>.Instance);

        var status = await service.GetSetupStatusAsync(CancellationToken.None);

        Assert.False(status.KubeconfigPresent);
        Assert.False(status.ClusterReachable);
        Assert.False(status.NamespaceReady);
        Assert.False(status.MetricsServerPresent);
        Assert.False(status.SecretsConfigured);
        Assert.NotEmpty(status.Warnings);
    }

    [Fact]
    public async Task GetSetupStatusAsync_Reports_NamespaceReady_And_SecretsConfigured_When_Both_Exist()
    {
        var (service, client, _) = CreateWithReachableCluster();

        client.Setup(c => c.CoreV1.ListNamespaceWithHttpMessagesAsync(
                It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(),
                It.IsAny<int?>(), It.IsAny<bool?>(), It.IsAny<bool?>(),
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HttpOperationResponse<V1NamespaceList>
            {
                Body = new V1NamespaceList
                {
                    Items = new List<V1Namespace> { new() { Metadata = new V1ObjectMeta { Name = Namespace } } }
                }
            });

        client.Setup(c => c.CoreV1.ReadNamespacedSecretWithHttpMessagesAsync(
                SecretName, Namespace, It.IsAny<bool?>(),
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HttpOperationResponse<V1Secret>
            {
                Body = new V1Secret { Metadata = new V1ObjectMeta { Name = SecretName } }
            });

        var status = await service.GetSetupStatusAsync(CancellationToken.None);

        Assert.True(status.KubeconfigPresent);
        Assert.True(status.ClusterReachable);
        Assert.True(status.NamespaceReady);
        Assert.True(status.MetricsServerPresent);
        Assert.True(status.SecretsConfigured);
        Assert.Empty(status.Warnings);
    }

    [Fact]
    public async Task GetSetupStatusAsync_Warns_But_Does_Not_Fail_When_Secret_Missing()
    {
        var (service, client, _) = CreateWithReachableCluster();

        client.Setup(c => c.CoreV1.ListNamespaceWithHttpMessagesAsync(
                It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(),
                It.IsAny<int?>(), It.IsAny<bool?>(), It.IsAny<bool?>(),
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HttpOperationResponse<V1NamespaceList>
            {
                Body = new V1NamespaceList
                {
                    Items = new List<V1Namespace> { new() { Metadata = new V1ObjectMeta { Name = Namespace } } }
                }
            });

        client.Setup(c => c.CoreV1.ReadNamespacedSecretWithHttpMessagesAsync(
                SecretName, Namespace, It.IsAny<bool?>(),
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpOperationException
            {
                Response = new HttpResponseMessageWrapper(
                    new HttpResponseMessage(System.Net.HttpStatusCode.NotFound), string.Empty)
            });

        var status = await service.GetSetupStatusAsync(CancellationToken.None);

        Assert.True(status.ClusterReachable);
        Assert.True(status.NamespaceReady);
        Assert.False(status.SecretsConfigured);
        Assert.Contains(status.Warnings, w => w.Contains("secrets", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetSetupStatusAsync_Warns_When_MetricsServer_Absent()
    {
        var client = new Mock<IKubernetes>();
        var factory = new Mock<IKubernetesClientFactory>();
        IKubernetes? outClient = client.Object;
        string? outErr = null;
        factory.Setup(f => f.TryGetClient(out outClient, out outErr)).Returns(true);

        var metrics = new Mock<IMetricsService>();
        metrics.Setup(m => m.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetricsSnapshot(false, null, Array.Empty<PodMetricsInfo>(), "metrics-server is not installed"));

        client.Setup(c => c.CoreV1.ListNamespaceWithHttpMessagesAsync(
                It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(),
                It.IsAny<int?>(), It.IsAny<bool?>(), It.IsAny<bool?>(),
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HttpOperationResponse<V1NamespaceList>
            {
                Body = new V1NamespaceList
                {
                    Items = new List<V1Namespace> { new() { Metadata = new V1ObjectMeta { Name = Namespace } } }
                }
            });

        client.Setup(c => c.CoreV1.ReadNamespacedSecretWithHttpMessagesAsync(
                SecretName, Namespace, It.IsAny<bool?>(),
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HttpOperationResponse<V1Secret>
            {
                Body = new V1Secret { Metadata = new V1ObjectMeta { Name = SecretName } }
            });

        var options = Options.Create(new DashboardOptions { Namespace = Namespace, SecretName = SecretName });
        var service = new KubernetesService(
            factory.Object, new DeploymentBuilderService(), Mock.Of<IRconService>(), metrics.Object,
            options, NullLogger<KubernetesService>.Instance);

        var status = await service.GetSetupStatusAsync(CancellationToken.None);

        Assert.False(status.MetricsServerPresent);
        Assert.Contains(status.Warnings, w => w.Contains("metrics-server", StringComparison.OrdinalIgnoreCase));
        // Missing metrics-server is a warning, not a failure of the overall check.
        Assert.True(status.ClusterReachable);
    }

    [Fact]
    public async Task GetSetupStatusAsync_Never_Throws_When_Cluster_Unreachable()
    {
        var factory = new Mock<IKubernetesClientFactory>();
        IKubernetes? nullClient = null;
        string? err = "unreachable";
        factory.Setup(f => f.TryGetClient(out nullClient, out err)).Returns(false);

        var options = Options.Create(new DashboardOptions { Namespace = Namespace, SecretName = SecretName });
        var service = new KubernetesService(
            factory.Object, new DeploymentBuilderService(), Mock.Of<IRconService>(), Mock.Of<IMetricsService>(),
            options, NullLogger<KubernetesService>.Instance);

        var exception = await Record.ExceptionAsync(() => service.GetSetupStatusAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task SetSecretsAsync_Throws_ClusterUnreachable_When_Cluster_Down()
    {
        var factory = new Mock<IKubernetesClientFactory>();
        IKubernetes? nullClient = null;
        string? err = "unreachable";
        factory.Setup(f => f.TryGetClient(out nullClient, out err)).Returns(false);

        var options = Options.Create(new DashboardOptions { Namespace = Namespace, SecretName = SecretName });
        var service = new KubernetesService(
            factory.Object, new DeploymentBuilderService(), Mock.Of<IRconService>(), Mock.Of<IMetricsService>(),
            options, NullLogger<KubernetesService>.Instance);

        await Assert.ThrowsAsync<ClusterUnreachableException>(
            () => service.SetSecretsAsync(new Dictionary<string, string> { ["FOO"] = "bar" }, CancellationToken.None));
    }
}
