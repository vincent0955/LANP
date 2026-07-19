using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using GameDashboard.Api.Models;
using GameDashboard.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace GameDashboard.Api.Controllers;

/// <summary>
/// Reports the host's LAN address(es) and public IP so the frontend can show
/// players ready-to-copy "ip:port" join addresses. The webview cannot discover
/// these itself; the backend runs on the host machine and can.
/// </summary>
[ApiController]
[Route("api/network")]
public sealed class NetworkController : ControllerBase
{
    private readonly IPublicIpService _publicIpService;

    public NetworkController(IPublicIpService publicIpService)
    {
        _publicIpService = publicIpService;
    }

    [HttpGet]
    public async Task<IActionResult> GetNetworkInfo(CancellationToken ct)
    {
        var lanAddresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic =>
                nic.OperationalStatus == OperationalStatus.Up &&
                nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .Select(nic => nic.GetIPProperties())
            // A default gateway is what separates the real LAN adapter from
            // virtual ones (Docker/WSL/Hyper-V vEthernet), whose addresses
            // other machines on the network cannot reach.
            .Where(props => props.GatewayAddresses.Count > 0)
            .SelectMany(props => props.UnicastAddresses)
            .Where(addr =>
                addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(addr.Address))
            .Select(addr => addr.Address.ToString())
            .Distinct()
            .ToList();

        var publicAddress = await _publicIpService.GetPublicIpAsync(ct);

        return Ok(new NetworkInfo(
            lanAddresses,
            publicAddress,
            Services.Docker.ContainerSpecBuilder.HostPortRangeStart,
            Services.Docker.ContainerSpecBuilder.HostPortRangeEnd));
    }
}
