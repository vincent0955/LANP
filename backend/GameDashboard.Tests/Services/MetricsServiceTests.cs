using GameDashboard.Api.Configuration;
using GameDashboard.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace GameDashboard.Tests.Services;

/// <summary>
/// Covers MetricsService's availability-detection guard logic (cluster unreachable
/// case). The metrics-server-present/absent branches require a real cluster response
/// shape from the aggregated metrics API and are exercised in the live verification
/// pass instead of being mocked here (the k8s client's metrics extension methods are
/// not virtual/mockable through the public IKubernetes surface in a way that's worth
/// the brittleness of faking their HTTP responses).
/// </summary>
public class MetricsServiceTests
{
    private const string Namespace = "game-servers";

    [Fact]
    public async Task GetSnapshotAsync_Reports_Unavailable_When_Cluster_Unreachable()
    {
        var factory = new Mock<IKubernetesClientFactory>();
        k8s.IKubernetes? nullClient = null;
        string? err = "cluster unreachable";
        factory.Setup(f => f.TryGetClient(out nullClient, out err)).Returns(false);

        var options = Options.Create(new DashboardOptions { Namespace = Namespace });
        var service = new MetricsService(factory.Object, options, NullLogger<MetricsService>.Instance);

        var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        Assert.False(snapshot.Available);
        Assert.Null(snapshot.Node);
        Assert.Empty(snapshot.Pods);
        Assert.NotNull(snapshot.UnavailableReason);
    }

    [Fact]
    public async Task GetSnapshotAsync_Does_Not_Throw_When_Cluster_Unreachable()
    {
        var factory = new Mock<IKubernetesClientFactory>();
        k8s.IKubernetes? nullClient = null;
        string? err = "cluster unreachable";
        factory.Setup(f => f.TryGetClient(out nullClient, out err)).Returns(false);

        var options = Options.Create(new DashboardOptions { Namespace = Namespace });
        var service = new MetricsService(factory.Object, options, NullLogger<MetricsService>.Instance);

        // The whole contract of IMetricsService is that it never throws (Req 9.3);
        // this assertion is the point of the test, not incidental.
        var exception = await Record.ExceptionAsync(() => service.GetSnapshotAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public void MetricsAvailable_Defaults_To_False_Before_Any_Query()
    {
        var factory = new Mock<IKubernetesClientFactory>();
        var options = Options.Create(new DashboardOptions { Namespace = Namespace });
        var service = new MetricsService(factory.Object, options, NullLogger<MetricsService>.Instance);

        Assert.False(service.MetricsAvailable);
    }

    [Fact]
    public async Task MetricsAvailable_Reflects_Unreachable_Cluster_After_Query()
    {
        var factory = new Mock<IKubernetesClientFactory>();
        k8s.IKubernetes? nullClient = null;
        string? err = "cluster unreachable";
        factory.Setup(f => f.TryGetClient(out nullClient, out err)).Returns(false);

        var options = Options.Create(new DashboardOptions { Namespace = Namespace });
        var service = new MetricsService(factory.Object, options, NullLogger<MetricsService>.Instance);

        await service.GetSnapshotAsync(CancellationToken.None);

        Assert.False(service.MetricsAvailable);
    }
}
