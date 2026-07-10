using GameDashboard.Api.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GameDashboard.Api.Middleware;

/// <summary>
/// Enforces a shared-secret token on every request when the backend is bound to a
/// non-loopback address. See requirements.md → Req 14.1, Req 14.6.
///
/// When bound to loopback (the default), this middleware is a no-op — no token is
/// required, matching the "plug and play, localhost-first" design. The moment the
/// operator configures a non-loopback BindAddress (to reach the dashboard from
/// another machine), a token becomes mandatory and every request must present it
/// via the "X-Api-Token" header. Startup itself fails fast if exposed without a
/// configured token, rather than silently running unauthenticated.
/// </summary>
public sealed class TokenAuthMiddleware
{
    private const string TokenHeaderName = "X-Api-Token";

    private readonly RequestDelegate _next;
    private readonly DashboardOptions _options;

    public TokenAuthMiddleware(RequestDelegate next, IOptions<DashboardOptions> options)
    {
        _next = next;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_options.IsLoopbackBind || !_options.RequireAuthWhenExposed)
        {
            await _next(context);
            return;
        }

        var providedToken = context.Request.Headers[TokenHeaderName].FirstOrDefault();

        if (string.IsNullOrEmpty(_options.ApiToken) ||
            string.IsNullOrEmpty(providedToken) ||
            !FixedTimeEquals(providedToken, _options.ApiToken))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Missing or invalid API token.",
                Type = "https://httpstatuses.io/401",
                Instance = context.Request.Path
            });
            return;
        }

        await _next(context);
    }

    /// <summary>
    /// Constant-time string comparison to avoid leaking token length/prefix
    /// information via response-time side channels.
    /// </summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        var bytesA = System.Text.Encoding.UTF8.GetBytes(a);
        var bytesB = System.Text.Encoding.UTF8.GetBytes(b);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(bytesA, bytesB);
    }
}
