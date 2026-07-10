using Microsoft.AspNetCore.Mvc;

namespace GameDashboard.Api.Middleware;

/// <summary>
/// Catches unhandled exceptions and returns a structured RFC 7807 ProblemDetails response.
/// See requirements.md → Req 14 (structured error responses).
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _env;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger,
        IHostEnvironment env)
    {
        _next = next;
        _logger = logger;
        _env = env;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception processing {Method} {Path}",
                context.Request.Method, context.Request.Path);

            await WriteProblemAsync(context, ex);
        }
    }

    private async Task WriteProblemAsync(HttpContext context, Exception ex)
    {
        var (status, title) = MapException(ex);

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Type = $"https://httpstatuses.io/{status}",
            Instance = context.Request.Path
        };

        // Only surface exception detail outside production; never leak internals to clients otherwise.
        if (_env.IsDevelopment())
        {
            problem.Detail = ex.Message;
        }

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(problem);
    }

    /// <summary>
    /// Maps well-known exception types to HTTP status codes. Extended in later phases
    /// as Kubernetes/RCON-specific exceptions are introduced.
    /// </summary>
    private static (int Status, string Title) MapException(Exception ex) => ex switch
    {
        GameDashboard.Api.Exceptions.ClusterUnreachableException =>
            (StatusCodes.Status503ServiceUnavailable, "Kubernetes cluster is unreachable."),
        GameDashboard.Api.Exceptions.RconUnavailableException =>
            (StatusCodes.Status503ServiceUnavailable, "RCON is unavailable for this server."),
        GameDashboard.Api.Exceptions.ServerAlreadyExistsException =>
            (StatusCodes.Status409Conflict, "Server already exists."),
        GameDashboard.Api.Exceptions.ServerNotFoundException =>
            (StatusCodes.Status404NotFound, "Server not found."),
        GameDashboard.Api.Exceptions.UnknownGameException =>
            (StatusCodes.Status400BadRequest, "Unknown game."),
        ArgumentException or FormatException =>
            (StatusCodes.Status400BadRequest, "Invalid request."),
        KeyNotFoundException =>
            (StatusCodes.Status404NotFound, "Resource not found."),
        InvalidOperationException =>
            (StatusCodes.Status409Conflict, "Operation could not be completed."),
        NotImplementedException =>
            (StatusCodes.Status501NotImplemented, "Not implemented."),
        _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.")
    };
}
