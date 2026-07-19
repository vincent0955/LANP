using System.Net;
using System.Net.Http.Json;
using GameDashboard.Api.GameTemplates;
using GameDashboard.Api.Models;
using Xunit;

namespace GameDashboard.Tests.Integration;

/// <summary>
/// Failure-path integration tests against a real, reachable Docker engine.
/// Covers the degradation behaviors that matter most for this backend: RCON
/// unreachability for a server that doesn't exist, and an empty catalog
/// search — all of which must degrade gracefully rather than error.
///
/// Engine-down itself is intentionally NOT simulated by stopping the engine
/// (that would make the suite destructive to run) — that path is covered by the
/// unit tests in DockerServiceTests/DockerMetricsServiceTests/RconServiceTests
/// using a mocked unreachable client, which exercises the exact same code path.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public class FailurePathIntegrationTests
{
    private readonly HttpClient _client;

    public FailurePathIntegrationTests(IntegrationTestFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task Metrics_Endpoint_Returns_A_Well_Formed_Snapshot()
    {
        // docker stats is built into the engine, so with a reachable engine the
        // snapshot should be Available; if it isn't (engine hiccup), the
        // contract is graceful degradation, never a 500.
        var response = await _client.GetAsync("/api/metrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var snapshot = await response.Content.ReadFromJsonAsync<MetricsSnapshot>(IntegrationTestFixture.JsonOptions);
        Assert.NotNull(snapshot);

        if (snapshot!.Available)
        {
            Assert.NotNull(snapshot.Node);
            Assert.True(snapshot.Node!.MemCapacityBytes > 0);
        }
        else
        {
            Assert.Null(snapshot.Node);
            Assert.Empty(snapshot.Pods);
            Assert.NotNull(snapshot.UnavailableReason);
        }
    }

    [Fact]
    public async Task SetupStatus_Reports_The_Engine_Reachable()
    {
        var response = await _client.GetAsync("/api/setup/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var status = await response.Content.ReadFromJsonAsync<SetupStatus>(IntegrationTestFixture.JsonOptions);
        Assert.NotNull(status);
        Assert.True(status!.DockerEngineReachable);
        // Metrics are available exactly when the engine is (no metrics-server
        // equivalent exists to be missing anymore).
        Assert.True(status.MetricsAvailable);
    }

    [Fact]
    public async Task RconCommand_Returns_ServiceUnavailable_For_Server_Without_Rcon_Configured()
    {
        // The documented contract: an unreachable/unconfigured RCON target
        // returns 503 (or 404 for a server that doesn't exist at all), never a
        // raw exception or unrelated status.
        var response = await _client.PostAsJsonAsync(
            "/api/servers/insurgency-server/rcon", new { command = "status" });

        Assert.True(
            response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.NotFound,
            $"Expected 503 (RCON unavailable) or 404 (server not found), got {response.StatusCode}.");
    }

    [Fact]
    public async Task GamesEndpoint_Returns_Empty_Array_For_Search_With_No_Matches()
    {
        var response = await _client.GetAsync("/api/games?search=ThisGameDefinitelyDoesNotExist12345");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var games = await response.Content.ReadFromJsonAsync<List<GameTemplate>>(IntegrationTestFixture.JsonOptions);
        Assert.NotNull(games);
        Assert.Empty(games!);
    }

    [Fact]
    public async Task GamesEndpoint_Returns_All_TwentyOne_Games_Without_Search()
    {
        var response = await _client.GetAsync("/api/games");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var games = await response.Content.ReadFromJsonAsync<List<GameTemplate>>(IntegrationTestFixture.JsonOptions);
        Assert.NotNull(games);
        Assert.Equal(21, games!.Count);
    }

    [Fact]
    public async Task Health_Endpoint_Never_Errors_Regardless_Of_Engine_State()
    {
        var response = await _client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var health = await response.Content.ReadFromJsonAsync<ClusterHealth>(IntegrationTestFixture.JsonOptions);
        Assert.NotNull(health);
    }
}
