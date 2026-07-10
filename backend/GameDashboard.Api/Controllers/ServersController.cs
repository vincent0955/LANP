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
    private readonly IKubernetesService _kubernetesService;
    private readonly IRconService _rconService;

    public ServersController(IKubernetesService kubernetesService, IRconService rconService)
    {
        _kubernetesService = kubernetesService;
        _rconService = rconService;
    }

    [HttpGet]
    public async Task<IActionResult> ListServers(CancellationToken ct)
    {
        var servers = await _kubernetesService.ListServersAsync(ct);
        return Ok(servers);
    }

    [HttpGet("{name}")]
    public async Task<IActionResult> GetServer(string name, CancellationToken ct)
    {
        if (!ServerNameValidator.IsValid(name))
        {
            return InvalidNameProblem(name);
        }

        var server = await _kubernetesService.GetServerAsync(name, ct);
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

        var created = await _kubernetesService.DeployServerAsync(request, ct);
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

        await _kubernetesService.ScaleServerAsync(name, request.Replicas, ct);
        var updated = await _kubernetesService.GetServerAsync(name, ct);
        return Ok(updated);
    }

    [HttpDelete("{name}")]
    public async Task<IActionResult> DeleteServer(string name, [FromQuery] bool deleteData, CancellationToken ct)
    {
        if (!ServerNameValidator.IsValid(name))
        {
            return InvalidNameProblem(name);
        }

        await _kubernetesService.DeleteServerAsync(name, deleteData, ct);
        return NoContent();
    }

    [HttpGet("{name}/config")]
    public async Task<IActionResult> GetConfig(string name, CancellationToken ct)
    {
        if (!ServerNameValidator.IsValid(name))
        {
            return InvalidNameProblem(name);
        }

        var config = await _kubernetesService.GetConfigAsync(name, ct);
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

        await _kubernetesService.UpdateConfigAsync(name, values, ct);
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
