using GameDashboard.Api.Configuration;
using GameDashboard.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace GameDashboard.Tests.Middleware;

/// <summary>
/// Covers TokenAuthMiddleware's enforcement rules (Req 14.1, Req 14.6).
/// </summary>
public class TokenAuthMiddlewareTests
{
    private static (TokenAuthMiddleware Middleware, DefaultHttpContext Context, bool NextCalled) Create(
        DashboardOptions options, string? providedToken = null)
    {
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };

        var middleware = new TokenAuthMiddleware(next, Options.Create(options));
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        if (providedToken is not null)
        {
            context.Request.Headers["X-Api-Token"] = providedToken;
        }

        return (middleware, context, nextCalled);
    }

    [Fact]
    public async Task Allows_Request_When_Bound_To_Loopback_Even_Without_Token()
    {
        var options = new DashboardOptions { BindAddress = "127.0.0.1", ApiToken = null };
        var (middleware, context, _) = Create(options);

        await middleware.InvokeAsync(context);

        Assert.Equal(200, context.Response.StatusCode == 0 ? 200 : context.Response.StatusCode);
    }

    [Fact]
    public async Task Calls_Next_When_Bound_To_Loopback()
    {
        var called = false;
        RequestDelegate next = _ => { called = true; return Task.CompletedTask; };
        var middleware = new TokenAuthMiddleware(next, Options.Create(
            new DashboardOptions { BindAddress = "127.0.0.1" }));

        await middleware.InvokeAsync(new DefaultHttpContext());

        Assert.True(called);
    }

    [Fact]
    public async Task Calls_Next_When_RequireAuthWhenExposed_Is_False_Even_On_NonLoopback()
    {
        var called = false;
        RequestDelegate next = _ => { called = true; return Task.CompletedTask; };
        var middleware = new TokenAuthMiddleware(next, Options.Create(
            new DashboardOptions { BindAddress = "0.0.0.0", RequireAuthWhenExposed = false }));

        await middleware.InvokeAsync(new DefaultHttpContext());

        Assert.True(called);
    }

    [Fact]
    public async Task Rejects_Request_On_NonLoopback_Without_Token_Header()
    {
        var called = false;
        RequestDelegate next = _ => { called = true; return Task.CompletedTask; };
        var middleware = new TokenAuthMiddleware(next, Options.Create(
            new DashboardOptions { BindAddress = "0.0.0.0", ApiToken = "secret-token" }));

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.False(called);
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task Rejects_Request_On_NonLoopback_With_Wrong_Token()
    {
        var called = false;
        RequestDelegate next = _ => { called = true; return Task.CompletedTask; };
        var middleware = new TokenAuthMiddleware(next, Options.Create(
            new DashboardOptions { BindAddress = "0.0.0.0", ApiToken = "correct-token" }));

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Headers["X-Api-Token"] = "wrong-token";

        await middleware.InvokeAsync(context);

        Assert.False(called);
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task Allows_Request_On_NonLoopback_With_Correct_Token()
    {
        var called = false;
        RequestDelegate next = _ => { called = true; return Task.CompletedTask; };
        var middleware = new TokenAuthMiddleware(next, Options.Create(
            new DashboardOptions { BindAddress = "0.0.0.0", ApiToken = "correct-token" }));

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Headers["X-Api-Token"] = "correct-token";

        await middleware.InvokeAsync(context);

        Assert.True(called);
    }

    [Fact]
    public async Task Rejects_Request_When_NonLoopback_And_No_Token_Configured_At_All()
    {
        // Defense in depth: even if the fail-fast startup check were somehow
        // bypassed, the middleware itself must still refuse rather than allow.
        var called = false;
        RequestDelegate next = _ => { called = true; return Task.CompletedTask; };
        var middleware = new TokenAuthMiddleware(next, Options.Create(
            new DashboardOptions { BindAddress = "0.0.0.0", ApiToken = null }));

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Headers["X-Api-Token"] = "anything";

        await middleware.InvokeAsync(context);

        Assert.False(called);
        Assert.Equal(401, context.Response.StatusCode);
    }
}
