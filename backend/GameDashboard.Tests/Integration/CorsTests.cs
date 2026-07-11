using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace GameDashboard.Tests.Integration;

/// <summary>
/// Contract tests for the CORS policy required by the desktop frontend: the Tauri
/// webview (http://tauri.localhost) and the Vite dev server (http://localhost:5173)
/// are cross-origin to the backend, so REST fetches and the SignalR /negotiate
/// handshake depend on these headers. Follows the RootEndpointTests pattern — boots
/// the real pipeline via WebApplicationFactory but needs no reachable cluster, so
/// it runs in the default (non-Integration) suite.
/// </summary>
public class CorsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public CorsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("http://tauri.localhost")]
    [InlineData("http://localhost:5173")]
    [InlineData("http://127.0.0.1:5173")]
    public async Task Allowed_Origin_Gets_Cors_Headers_On_Simple_Request(string origin)
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Origin", origin);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        // AllowCredentials is required because the SignalR JS client sends
        // credentialed requests during negotiation.
        Assert.Equal("true", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials")));
    }

    [Fact]
    public async Task Preflight_From_Allowed_Origin_Succeeds_With_Method_And_Headers()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/servers");
        request.Headers.Add("Origin", "http://tauri.localhost");
        request.Headers.Add("Access-Control-Request-Method", "PUT");
        // Custom headers the frontend sends: content type on writes, and the
        // optional auth token. Preflights themselves never carry X-Api-Token, so
        // CORS middleware must answer them before TokenAuthMiddleware runs.
        request.Headers.Add("Access-Control-Request-Headers", "content-type,x-api-token");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("http://tauri.localhost",
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains("PUT",
            string.Join(",", response.Headers.GetValues("Access-Control-Allow-Methods")));
        var allowedHeaders = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Contains("content-type", allowedHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x-api-token", allowedHeaders, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Disallowed_Origin_Gets_No_Cors_Headers()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Origin", "http://evil.example");

        var response = await client.SendAsync(request);

        // The request itself still succeeds (CORS is enforced by the browser, not
        // the server) — but no Access-Control-Allow-Origin header may be emitted.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
