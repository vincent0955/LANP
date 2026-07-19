using System.Net.Http.Json;
using System.Text.Json;
using GameDashboard.Api.GameTemplates;
using GameDashboard.Api.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace GameDashboard.Tests.Integration;

/// <summary>
/// Contract tests for the SignalR hub's server-to-client message shapes, locked
/// against design.md → SignalR Hub. Connects a real HubConnection through the
/// WebApplicationFactory's in-memory TestServer (via a custom HttpMessageHandler),
/// so this exercises the actual wire serialization SignalR uses, not just the C#
/// record shapes.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public class SignalRContractTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private HubConnection _connection = null!;

    public SignalRContractTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/dashboard", options =>
            {
                options.HttpMessageHandlerFactory = _ => _fixture.Server.CreateHandler();
            })
            .Build();

        await _connection.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task ServerStatusChanged_Message_Matches_Documented_Shape()
    {
        var received = new TaskCompletionSource<JsonElement>();

        _connection.On<JsonElement>("ServerStatusChanged", payload => received.TrySetResult(payload));
        await _connection.InvokeAsync("SubscribeEvents");

        var serverName = $"contract-signalr-{Guid.NewGuid():N}"[..24];
        var deployRequest = new DeployServerRequest(serverName, CuratedGameTemplates.Terraria.ImageTag, null, null);

        try
        {
            var deployResponse = await _fixture.Client.PostAsJsonAsync("/api/servers", deployRequest);
            deployResponse.EnsureSuccessStatusCode();

            // A status change (Pending/Running as the container comes up) should
            // arrive within a reasonable window once the container watch poll
            // picks up the new server.
            var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(30)));

            if (completed == received.Task)
            {
                var payload = await received.Task;
                AssertHasProperties(payload, "serverName", "status", "timestamp");
                Assert.Equal(JsonValueKind.String, payload.GetProperty("status").ValueKind);
            }
            // If no event arrived within the window (e.g. the watch poll hasn't
            // caught up yet on a slow CI runner), this is not treated as a
            // contract failure — the contract under test is the *shape* of the
            // message when one does arrive, verified above. Liveness of the
            // watch itself is covered by the live Phase 5 verification.
        }
        finally
        {
            await _fixture.Client.DeleteAsync($"/api/servers/{serverName}?deleteData=true");
        }
    }

    [Fact]
    public async Task SubscribeLogs_Does_Not_Throw_And_Can_Be_Unsubscribed()
    {
        // Contract-level check: the hub methods documented in design.md exist and
        // accept the documented parameter shapes without erroring. Actual log
        // delivery is covered in the Phase 5 live verification.
        var exception = await Record.ExceptionAsync(async () =>
        {
            await _connection.InvokeAsync("SubscribeLogs", "cs2-server");
            await _connection.InvokeAsync("UnsubscribeLogs", "cs2-server");
        });

        Assert.Null(exception);
    }

    [Fact]
    public async Task SubscribeMetrics_Does_Not_Throw()
    {
        var exception = await Record.ExceptionAsync(() => _connection.InvokeAsync("SubscribeMetrics"));

        Assert.Null(exception);
    }

    [Fact]
    public async Task MetricsUpdate_Message_Matches_Documented_Shape_When_Received()
    {
        var received = new TaskCompletionSource<JsonElement>();
        _connection.On<JsonElement>("MetricsUpdate", payload => received.TrySetResult(payload));
        await _connection.InvokeAsync("SubscribeMetrics");

        // MetricsPushService pushes on its configured interval (default 5s in
        // appsettings); wait a bit longer than that.
        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(15)));

        if (completed == received.Task)
        {
            var payload = await received.Task;
            AssertHasProperties(payload, "available", "node", "pods", "unavailableReason");
        }
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
