using GameDashboard.Api.Models;
using GameDashboard.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace GameDashboard.Api.Controllers;

/// <summary>
/// See requirements.md → Req 2 (list/get), Req 3 (deploy), Req 4 (scale),
/// Req 5 (delete), Req 6 (config), Req 10.5 (RCON command), Req 14.2 (structured
/// ProblemDetails error responses), Req 14.5 (input validation/sanitization
/// across all endpoints).
///
/// All error responses use ControllerBase.Problem()/ValidationProblem() so the
/// wire shape matches ExceptionHandlingMiddleware's RFC 7807 ProblemDetails
/// output exactly, rather than ad-hoc anonymous objects — verified by
/// Integration/ApiContractTests.cs.
/// </summary>
[ApiController]
[Route("api/servers")]
public sealed class ServersController : ControllerBase
{
    private readonly IServerOrchestrator _orchestrator;
    private readonly IRconService _rconService;

    public ServersController(IServerOrchestrator orchestrator, IRconService rconService)
    {
        _orchestrator = orchestrator;
        _rconService = rconService;
    }

    [HttpGet]
    public async Task<IActionResult> ListServers(CancellationToken ct)
    {
        var servers = await _orchestrator.ListServersAsync(ct);
        return Ok(servers);
    }

    [HttpGet("{name}")]
    public async Task<IActionResult> GetServer(string name, CancellationToken ct)
    {
        if (!ServerNameValidator.IsValid(name))
        {
            return InvalidNameProblem(name);
        }

        var server = await _orchestrator.GetServerAsync(name, ct);
        if (server is null)
        {
            return Problem(
                title: "Server not found.",
                detail: $"Server '{name}' was not found.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Ok(server);
    }

    [HttpPost]
    public async Task<IActionResult> DeployServer([FromBody] DeployServerRequest? request, CancellationToken ct)
    {
        if (request is null)
        {
            return Problem(
                title: "Invalid request.", detail: "A request body is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(request.ImageTag))
        {
            return Problem(
                title: "Invalid request.", detail: "imageTag is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var created = await _orchestrator.DeployServerAsync(request, ct);
        return CreatedAtAction(nameof(GetServer), new { name = created.Name }, created);
    }

    [HttpPost("{name}/scale")]
    public async Task<IActionResult> ScaleServer(string name, [FromBody] ScaleRequest? request, CancellationToken ct)
    {
        if (!ServerNameValidator.IsValid(name))
        {
            return InvalidNameProblem(name);
        }

        if (request is null)
        {
            return Problem(
                title: "Invalid request.", detail: "A request body with a 'replicas' value is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        await _orchestrator.ScaleServerAsync(name, request.Replicas, ct);
        var updated = await _orchestrator.GetServerAsync(name, ct);
        return Ok(updated);
    }

    [HttpDelete("{name}")]
    public async Task<IActionResult> DeleteServer(string name, [FromQuery] bool deleteData, CancellationToken ct)
    {
        if (!ServerNameValidator.IsValid(name))
        {
            return InvalidNameProblem(name);
        }

        await _orchestrator.DeleteServerAsync(name, deleteData, ct);
        return NoContent();
    }

    [HttpGet("{name}/config")]
    public async Task<IActionResult> GetConfig(string name, CancellationToken ct)
    {
        if (!ServerNameValidator.IsValid(name))
        {
            return InvalidNameProblem(name);
        }

        var config = await _orchestrator.GetConfigAsync(name, ct);
        return Ok(config);
    }

    [HttpPut("{name}/config")]
    public async Task<IActionResult> UpdateConfig(
        string name, [FromBody] IDictionary<string, string>? values, CancellationToken ct)
    {
        if (!ServerNameValidator.IsValid(name))
        {
            return InvalidNameProblem(name);
        }

        if (values is null || values.Count == 0)
        {
            return Problem(
                title: "Invalid request.", detail: "At least one config key/value pair is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        await _orchestrator.UpdateConfigAsync(name, values, ct);
        return NoContent();
    }

    /// <summary>
    /// Lists the secrets this server's game requires and whether each is set.
    /// Values are never returned here — reveal is a separate per-key request.
    /// </summary>
    [HttpGet("{name}/secrets")]
    public async Task<IActionResult> GetServerSecrets(string name, CancellationToken ct)
    {
        if (!ServerNameValidator.IsValid(name))
        {
            return InvalidNameProblem(name);
        }

        var secrets = await _orchestrator.GetServerSecretsAsync(name, ct);
        return Ok(secrets);
    }

    /// <summary>
    /// Sets one or more of this server's secrets and recreates its container so
    /// the new values take effect. Only keys the server's game uses are accepted.
    /// </summary>
    [HttpPost("{name}/secrets")]
    public async Task<IActionResult> SetServerSecrets(
        string name, [FromBody] IDictionary<string, string>? values, CancellationToken ct)
    {
        if (!ServerNameValidator.IsValid(name))
        {
            return InvalidNameProblem(name);
        }

        if (values is null || values.Count == 0)
        {
            return Problem(
                title: "Invalid request.", detail: "At least one secret key/value pair is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        await _orchestrator.SetServerSecretsAsync(name, values, ct);
        return NoContent();
    }

    /// <summary>
    /// Reveals a single secret value for the server page's click-to-show button.
    /// 404 when no value is set for that key on this server.
    /// </summary>
    [HttpGet("{name}/secrets/{key}/value")]
    public async Task<IActionResult> GetServerSecretValue(string name, string key, CancellationToken ct)
    {
        if (!ServerNameValidator.IsValid(name))
        {
            return InvalidNameProblem(name);
        }

        var value = await _orchestrator.GetServerSecretValueAsync(name, key, ct);
        return Ok(new { value });
    }

    /// <summary>Clears a single secret for this server and recreates its container.</summary>
    [HttpDelete("{name}/secrets/{key}")]
    public async Task<IActionResult> DeleteServerSecret(string name, string key, CancellationToken ct)
    {
        if (!ServerNameValidator.IsValid(name))
        {
            return InvalidNameProblem(name);
        }

        await _orchestrator.DeleteServerSecretAsync(name, key, ct);
        return NoContent();
    }

    /// <summary>
    /// Regenerates an app-managed secret (e.g. an RCON password) for this server
    /// with a fresh strong value and recreates its container. 400 when the key is
    /// not app-managed on this server.
    /// </summary>
    [HttpPost("{name}/secrets/{key}/regenerate")]
    public async Task<IActionResult> RegenerateServerSecret(string name, string key, CancellationToken ct)
    {
        if (!ServerNameValidator.IsValid(name))
        {
            return InvalidNameProblem(name);
        }

        await _orchestrator.RegenerateServerSecretAsync(name, key, ct);
        return NoContent();
    }

    [HttpPost("{name}/rcon")]
    public async Task<IActionResult> SendRconCommand(
        string name, [FromBody] RconCommandRequest? request, CancellationToken ct)
    {
        if (!ServerNameValidator.IsValid(name))
        {
            return InvalidNameProblem(name);
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Command))
        {
            return Problem(
                title: "Invalid request.", detail: "A non-empty 'command' is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var response = await _rconService.SendCommandAsync(name, request.Command, ct);
        return Ok(new RconCommandResponse(response));
    }

    private ObjectResult InvalidNameProblem(string name) =>
        Problem(
            title: "Invalid request.",
            detail: $"'{name}' is not a valid server name.",
            statusCode: StatusCodes.Status400BadRequest);
}

/// <summary>Body for the scale endpoint.</summary>
public sealed record ScaleRequest(int Replicas);

/// <summary>Body for the RCON command endpoint.</summary>
public sealed record RconCommandRequest(string Command);

/// <summary>Response for the RCON command endpoint.</summary>
public sealed record RconCommandResponse(string Response);
