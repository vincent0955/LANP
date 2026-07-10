using System.Net.Http.Json;
using System.Text.Json;
using GameDashboard.Api.GameTemplates;
using GameDashboard.Api.Models;
using Xunit;

namespace GameDashboard.Tests.Integration;

/// <summary>
/// Contract tests: lock the REST API's JSON wire shape against the documented
/// models in design.md, independent of business-logic correctness (covered
/// elsewhere). These assert on raw JSON property presence/casing rather than
/// deserializing into the model type, so a property rename or removal is caught
/// even if it wouldn't otherwise fail deserialization (e.g. extra/missing
/// properties are usually silently ignored by System.Text.Json).
///
/// Run against the real cluster like the other integration tests (some endpoints
/// need a reachable cluster to return 200), but the assertions here are about
/// shape, not values — see design.md → API Surface, Data Models.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public class ApiContractTests
{
    private readonly HttpClient _client;

    public ApiContractTests(IntegrationTestFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task Health_Response_Matches_ClusterHealth_Shape()
    {
        var json = await GetJsonAsync("/api/health");

        AssertHasProperties(json, "clusterReachable", "namespaceReady", "namespace", "error");
    }

    [Fact]
    public async Task SetupStatus_Response_Matches_SetupStatus_Shape()
    {
        var json = await GetJsonAsync("/api/setup/status");

        AssertHasProperties(json,
            "kubeconfigPresent", "clusterReachable", "namespaceReady",
            "metricsServerPresent", "secretsConfigured", "warnings");

        Assert.Equal(JsonValueKind.Array, json.GetProperty("warnings").ValueKind);
    }

    [Fact]
    public async Task Metrics_Response_Matches_MetricsSnapshot_Shape()
    {
        var json = await GetJsonAsync("/api/metrics");

        AssertHasProperties(json, "available", "node", "pods", "unavailableReason");
        Assert.Equal(JsonValueKind.Array, json.GetProperty("pods").ValueKind);
    }

    [Fact]
    public async Task Games_Response_Is_An_Array_Of_GameTemplate_Shape()
    {
        var json = await GetJsonAsync("/api/games");

        Assert.Equal(JsonValueKind.Array, json.ValueKind);
        Assert.True(json.GetArrayLength() > 0);

        var first = json[0];
        AssertHasProperties(first,
            "displayName", "imageTag", "steamAppId", "dataMountPath",
            "defaultStorageBytes", "defaultPorts", "defaultResources",
            "defaultConfig", "secretKeyRefs");
    }

    [Fact]
    public async Task GameTemplate_DefaultPorts_Entries_Match_TemplatePort_Shape()
    {
        var json = await GetJsonAsync("/api/games?search=Minecraft");
        var minecraft = json[0];
        var ports = minecraft.GetProperty("defaultPorts");

        Assert.True(ports.GetArrayLength() > 0);
        AssertHasProperties(ports[0], "name", "protocol", "containerPort");
    }

    [Fact]
    public async Task GameTemplate_DefaultResources_Matches_ResourceSpec_Shape()
    {
        var json = await GetJsonAsync("/api/games?search=Minecraft");
        var resources = json[0].GetProperty("defaultResources");

        AssertHasProperties(resources, "cpuRequest", "cpuLimit", "memoryRequest", "memoryLimit");
    }

    [Fact]
    public async Task Servers_List_Response_Is_An_Array_Of_ServerSummary_Shape()
    {
        var json = await GetJsonAsync("/api/servers");

        Assert.Equal(JsonValueKind.Array, json.ValueKind);

        if (json.GetArrayLength() > 0)
        {
            AssertHasProperties(json[0], "name", "game", "image", "status", "replicas", "createdAt");
            // status must serialize as a string (JsonStringEnumConverter), not a raw int.
            Assert.Equal(JsonValueKind.String, json[0].GetProperty("status").ValueKind);
        }
    }

    [Fact]
    public async Task ServerDetail_Response_Matches_Documented_Shape()
    {
        var deployRequest = new DeployServerRequest(
            $"contract-{Guid.NewGuid():N}"[..20], CuratedGameTemplates.Terraria.ImageTag, null, null);

        var deployResponse = await _client.PostAsJsonAsync("/api/servers", deployRequest);
        deployResponse.EnsureSuccessStatusCode();

        try
        {
            using var stream = await deployResponse.Content.ReadAsStreamAsync();
            var json = await JsonDocument.ParseAsync(stream);
            var root = json.RootElement;

            AssertHasProperties(root,
                "name", "game", "image", "status", "replicas", "createdAt",
                "ports", "resources", "players");

            Assert.Equal(JsonValueKind.String, root.GetProperty("status").ValueKind);
            Assert.Equal(JsonValueKind.Array, root.GetProperty("ports").ValueKind);

            if (root.GetProperty("ports").GetArrayLength() > 0)
            {
                AssertHasProperties(root.GetProperty("ports")[0], "name", "protocol", "containerPort", "nodePort");
            }
        }
        finally
        {
            await _client.DeleteAsync($"/api/servers/{deployRequest.Name}?deleteData=true");
        }
    }

    [Fact]
    public async Task ProblemDetails_Error_Response_Has_Standard_Shape()
    {
        var response = await _client.GetAsync("/api/servers/this-does-not-exist-contract-test");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);

        using var stream = await response.Content.ReadAsStreamAsync();
        var json = await JsonDocument.ParseAsync(stream);

        // ASP.NET Core's ProblemDetails contract (RFC 7807): status/title present at minimum.
        AssertHasProperties(json.RootElement, "status", "title");
    }

    // --- helpers ---

    private async Task<JsonElement> GetJsonAsync(string url)
    {
        var response = await _client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync();
        var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement.Clone();
    }

    private static void AssertHasProperties(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            Assert.True(
                element.TryGetProperty(name, out _),
                $"Expected JSON property '{name}' was not found. Actual properties: " +
                string.Join(", ", element.EnumerateObject().Select(p => p.Name)));
        }
    }
}
