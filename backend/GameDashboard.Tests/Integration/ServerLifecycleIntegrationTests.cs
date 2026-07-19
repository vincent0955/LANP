using System.Net;
using System.Net.Http.Json;
using GameDashboard.Api.GameTemplates;
using GameDashboard.Api.Models;
using Xunit;

namespace GameDashboard.Tests.Integration;

/// <summary>
/// End-to-end lifecycle test against a real, reachable Docker engine.
/// Deploys a lightweight game (Terraria — small image, no license gate like
/// Minecraft's EULA, fast to reach a stable state) and exercises the full
/// deploy → status transitions → scale → config update → delete flow.
///
/// Deploys are asynchronous now (docs/docker-migration.md → Deploy is
/// asynchronous): POST /api/servers returns immediately with a Pending detail
/// while the image pull runs in the background, so the test waits for the
/// server to reach Running before exercising the write operations that need
/// the container to exist.
///
/// Requires a running Docker engine (Docker Desktop or the bundled runtime).
/// Not run by default; see IntegrationTestFixture.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public class ServerLifecycleIntegrationTests
{
    private readonly HttpClient _client;

    // Unique per test run so repeated runs (including a prior run that crashed
    // mid-test) never collide on a leftover resource.
    private readonly string _serverName = $"it-lifecycle-{Guid.NewGuid():N}"[..24];

    public ServerLifecycleIntegrationTests(IntegrationTestFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task Full_Lifecycle_Deploy_Scale_Config_Delete()
    {
        try
        {
            // --- Deploy (async: accepted immediately, pull in background) ---
            var deployRequest = new DeployServerRequest(
                _serverName, CuratedGameTemplates.Terraria.ImageTag, null, null);

            var deployResponse = await _client.PostAsJsonAsync("/api/servers", deployRequest);
            Assert.Equal(HttpStatusCode.Created, deployResponse.StatusCode);

            var created = await deployResponse.Content.ReadFromJsonAsync<ServerDetail>(IntegrationTestFixture.JsonOptions);
            Assert.NotNull(created);
            Assert.Equal(_serverName, created!.Name);
            Assert.NotEmpty(created.Ports);

            // --- Status transition: Pending (pulling/booting) or Running ---
            var initialStatus = await GetStatusAsync(_serverName);
            Assert.True(
                initialStatus is ServerStatus.Pending or ServerStatus.Running,
                $"Expected Pending or Running shortly after deploy, got {initialStatus}.");

            // --- Duplicate deploy is rejected (whether tracker or container holds the name) ---
            var duplicateResponse = await _client.PostAsJsonAsync("/api/servers", deployRequest);
            Assert.Equal(HttpStatusCode.Conflict, duplicateResponse.StatusCode);

            // --- Wait for the background deploy to finish and the game to come up
            // (first run pulls the image; the TCP readiness probe passes once the
            // server actually listens) ---
            await WaitForStatusAsync(_serverName, ServerStatus.Running, TimeSpan.FromMinutes(5));

            // --- Config: read, update, verify (update recreates the container) ---
            var configResponse = await _client.GetAsync($"/api/servers/{_serverName}/config");
            Assert.Equal(HttpStatusCode.OK, configResponse.StatusCode);
            var config = await configResponse.Content.ReadFromJsonAsync<Dictionary<string, string>>(IntegrationTestFixture.JsonOptions);
            Assert.NotNull(config);

            var updateResponse = await _client.PutAsJsonAsync(
                $"/api/servers/{_serverName}/config",
                new Dictionary<string, string> { ["IT_TEST_MARKER"] = "integration-test-value" });
            Assert.Equal(HttpStatusCode.NoContent, updateResponse.StatusCode);

            var updatedConfig = await (await _client.GetAsync($"/api/servers/{_serverName}/config"))
                .Content.ReadFromJsonAsync<Dictionary<string, string>>(IntegrationTestFixture.JsonOptions);
            Assert.Equal("integration-test-value", updatedConfig!["IT_TEST_MARKER"]);

            // --- Scale down ---
            var scaleDownResponse = await _client.PostAsJsonAsync(
                $"/api/servers/{_serverName}/scale", new { replicas = 0 });
            Assert.Equal(HttpStatusCode.OK, scaleDownResponse.StatusCode);

            // Graceful stop allows up to 30s for the world save before the kill.
            await WaitForStatusAsync(_serverName, ServerStatus.Stopped, TimeSpan.FromSeconds(60));

            // --- Scale back up ---
            var scaleUpResponse = await _client.PostAsJsonAsync(
                $"/api/servers/{_serverName}/scale", new { replicas = 1 });
            Assert.Equal(HttpStatusCode.OK, scaleUpResponse.StatusCode);

            var afterScaleUp = await GetStatusAsync(_serverName);
            Assert.True(afterScaleUp is ServerStatus.Pending or ServerStatus.Running);

            // --- Invalid scale value rejected ---
            var invalidScaleResponse = await _client.PostAsJsonAsync(
                $"/api/servers/{_serverName}/scale", new { replicas = 5 });
            Assert.Equal(HttpStatusCode.BadRequest, invalidScaleResponse.StatusCode);
        }
        finally
        {
            // --- Delete (idempotent; always attempt cleanup even on failure) ---
            var deleteResponse = await _client.DeleteAsync($"/api/servers/{_serverName}?deleteData=true");
            Assert.True(
                deleteResponse.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound,
                $"Expected successful cleanup delete, got {deleteResponse.StatusCode}.");

            // Idempotent: deleting again must not error.
            var secondDeleteResponse = await _client.DeleteAsync($"/api/servers/{_serverName}?deleteData=true");
            Assert.Equal(HttpStatusCode.NoContent, secondDeleteResponse.StatusCode);

            // Confirm it's actually gone.
            var getAfterDelete = await _client.GetAsync($"/api/servers/{_serverName}");
            Assert.Equal(HttpStatusCode.NotFound, getAfterDelete.StatusCode);
        }
    }

    [Fact]
    public async Task GetServer_Returns_NotFound_For_Nonexistent_Server()
    {
        var response = await _client.GetAsync("/api/servers/this-server-does-not-exist-abc123");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeployServer_Rejects_Unknown_Game()
    {
        var response = await _client.PostAsJsonAsync("/api/servers",
            new DeployServerRequest($"it-unknown-{Guid.NewGuid():N}"[..20], "not/a-real-game:latest", null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeployServer_Rejects_Invalid_Name()
    {
        var response = await _client.PostAsJsonAsync("/api/servers",
            new DeployServerRequest("Invalid_Name!", CuratedGameTemplates.Terraria.ImageTag, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<ServerStatus> GetStatusAsync(string name)
    {
        var response = await _client.GetAsync($"/api/servers/{name}");
        response.EnsureSuccessStatusCode();
        var detail = await response.Content.ReadFromJsonAsync<ServerDetail>(IntegrationTestFixture.JsonOptions);
        return detail!.Status;
    }

    private async Task WaitForStatusAsync(string name, ServerStatus expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        ServerStatus last = ServerStatus.Unknown;
        while (DateTime.UtcNow < deadline)
        {
            last = await GetStatusAsync(name);
            if (last == expected)
            {
                return;
            }
            if (last == ServerStatus.Error)
            {
                break; // a failed deploy never converges; fail fast with the status
            }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        Assert.Fail($"Server '{name}' did not reach status {expected} within {timeout.TotalSeconds}s (last: {last}).");
    }
}
