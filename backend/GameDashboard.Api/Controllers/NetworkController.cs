using GameDashboard.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace GameDashboard.Api.Controllers;

/// <summary>
/// Reports the host's LAN address(es) and public IP so the frontend can show
/// players ready-to-copy "ip:port" join addresses, plus per-server connectivity
/// diagnosis and forwarding instructions (WS2). The webview cannot discover any
/// of this itself; the backend runs on the host machine and can.
/// </summary>
[ApiController]
[Route("api/network")]
public sealed class NetworkController : ControllerBase
{
    private readonly IPublicIpService _publicIpService;
    private readonly INetworkDiagnosticsService _diagnostics;

    public NetworkController(IPublicIpService publicIpService, INetworkDiagnosticsService diagnostics)
    {
        _publicIpService = publicIpService;
        _diagnostics = diagnostics;
    }

    [HttpGet]
    public async Task<IActionResult> GetNetworkInfo(CancellationToken ct)
    {
        var lanAddresses = NetworkDiagnosticsService.GetLanAddresses();
        var publicAddress = await _publicIpService.GetPublicIpAsync(ct);

        return Ok(new Models.NetworkInfo(
            lanAddresses,
            publicAddress,
            Services.Docker.ContainerSpecBuilder.HostPortRangeStart,
            Services.Docker.ContainerSpecBuilder.HostPortRangeEnd));
    }

    /// <summary>
    /// Diagnoses whether a server is reachable by players: CGNAT detection plus a
    /// per-port local + best-effort outside-in probe. Never an error state —
    /// unverifiable ports report "unverified", not failure.
    /// </summary>
    [HttpGet("reachability/{name}")]
    public async Task<IActionResult> GetReachability(string name, CancellationToken ct)
    {
        if (!ServerNameValidator.IsValid(name))
        {
            return InvalidNameProblem(name);
        }

        return Ok(await _diagnostics.CheckReachabilityAsync(name, ct));
    }

    /// <summary>
    /// Diagnoses the whole forwardable port window in one shot, for users who
    /// forward all of it once instead of server by server. Independent of any
    /// server: sample ports are held open for the probe, so an empty range that
    /// is correctly forwarded still reports open.
    /// </summary>
    [HttpGet("reachability")]
    public async Task<IActionResult> GetRangeReachability(CancellationToken ct) =>
        Ok(await _diagnostics.CheckRangeReachabilityAsync(ct));

    /// <summary>
    /// Generates exact firewall + router-forwarding instructions for a server from
    /// its actually-allocated host ports.
    /// </summary>
    [HttpGet("servers/{name}/forwarding-guide")]
    public async Task<IActionResult> GetForwardingGuide(string name, CancellationToken ct)
    {
        if (!ServerNameValidator.IsValid(name))
        {
            return InvalidNameProblem(name);
        }

        return Ok(await _diagnostics.BuildForwardingGuideAsync(name, ct));
    }

    private ObjectResult InvalidNameProblem(string name) =>
        Problem(
            title: "Invalid request.",
            detail: $"'{name}' is not a valid server name.",
            statusCode: StatusCodes.Status400BadRequest);
}
