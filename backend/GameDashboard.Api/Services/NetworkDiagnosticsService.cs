using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using GameDashboard.Api.Exceptions;
using GameDashboard.Api.Models;

namespace GameDashboard.Api.Services;

/// <summary>
/// Network diagnostics for the "let friends join" story (WS2): CGNAT detection,
/// per-server reachability probing, and generation of exact firewall + router
/// forwarding instructions from a server's actually-allocated ports. The app
/// only diagnoses and instructs — it never changes the user's network itself.
/// </summary>
public interface INetworkDiagnosticsService
{
    Task<ServerReachability> CheckReachabilityAsync(string serverName, CancellationToken ct);

    /// <summary>
    /// Diagnoses the whole forwardable port window at once, independently of any
    /// server, for the "forward all 50 ports once" setup path.
    /// </summary>
    Task<RangeReachability> CheckRangeReachabilityAsync(CancellationToken ct);

    Task<ForwardingGuide> BuildForwardingGuideAsync(string serverName, CancellationToken ct);
}

public sealed class NetworkDiagnosticsService : INetworkDiagnosticsService
{
    private static readonly TimeSpan LocalDialTimeout = TimeSpan.FromMilliseconds(600);

    private readonly IServerOrchestrator _orchestrator;
    private readonly IPublicIpService _publicIpService;
    private readonly IExternalPortChecker _portChecker;

    public NetworkDiagnosticsService(
        IServerOrchestrator orchestrator,
        IPublicIpService publicIpService,
        IExternalPortChecker portChecker)
    {
        _orchestrator = orchestrator;
        _publicIpService = publicIpService;
        _portChecker = portChecker;
    }

    public async Task<ServerReachability> CheckReachabilityAsync(string serverName, CancellationToken ct)
    {
        var server = await _orchestrator.GetServerAsync(serverName, ct)
            ?? throw new ServerNotFoundException(serverName);

        var publicIp = await _publicIpService.GetPublicIpAsync(ct);
        var cgnat = DetectCgnat(publicIp);

        var ports = await Task.WhenAll(server.Ports.Select(async port =>
        {
            var isTcp = string.Equals(port.Protocol, "TCP", StringComparison.OrdinalIgnoreCase);
            var locallyListening = isTcp && await IsLocallyListeningAsync(port.NodePort, ct);

            var (external, detail) = await ClassifyExternalAsync(
                isTcp, port.NodePort, publicIp, cgnat.Detected, locallyListening, ct);

            return new PortReachability(
                port.Name, port.Protocol, port.NodePort, locallyListening, external, detail);
        }));

        return new ServerReachability(
            serverName, publicIp, cgnat.Detected, cgnat.Detail, ports);
    }

    public async Task<RangeReachability> CheckRangeReachabilityAsync(CancellationToken ct)
    {
        var start = Docker.ContainerSpecBuilder.HostPortRangeStart;
        var end = Docker.ContainerSpecBuilder.HostPortRangeEnd;

        var publicIp = await _publicIpService.GetPublicIpAsync(ct);
        var cgnat = DetectCgnat(publicIp);

        var ports = await Task.WhenAll(SampleRangePorts(start, end).Select(async port =>
        {
            // An idle port answers nothing, so probing the range as-is would report
            // every port without a server on it as Closed even with the forward in
            // place. Holding the port open for the length of the probe tests the
            // router + firewall path itself; a running server already holding it
            // serves the same purpose.
            using var held = HeldPort.TryHold(port);
            var locallyListening = held is not null || await IsLocallyListeningAsync(port, ct);

            var name = RangePortName(port, start, end);
            if (!locallyListening)
            {
                return new PortReachability(name, "TCP", port, false, ReachabilityState.Unverified,
                    "Couldn't open this port for the test — another program on this PC is using it.");
            }

            var (external, detail) = await ClassifyExternalAsync(
                isTcp: true, port, publicIp, cgnat.Detected, true, ct);

            return new PortReachability(name, "TCP", port, true, external, detail);
        }));

        return new RangeReachability(publicIp, cgnat.Detected, cgnat.Detail, start, end, ports);
    }

    /// <summary>
    /// The ports actually probed by a whole-range test: first, middle and last of
    /// the window. Router and firewall rules are written as ranges — they cover
    /// every port in the window or none — so three spread-out samples settle it
    /// without 50 external probes.
    /// </summary>
    internal static IReadOnlyList<int> SampleRangePorts(int start, int end) =>
        new[] { start, start + ((end - start) / 2), end }
            .Distinct()
            .OrderBy(p => p)
            .ToList();

    private static string RangePortName(int port, int start, int end) =>
        port == start ? "First port in range"
        : port == end ? "Last port in range"
        : "Middle of range";

    public async Task<ForwardingGuide> BuildForwardingGuideAsync(string serverName, CancellationToken ct)
    {
        var server = await _orchestrator.GetServerAsync(serverName, ct)
            ?? throw new ServerNotFoundException(serverName);

        var lanAddresses = GetLanAddresses();
        var publicIp = await _publicIpService.GetPublicIpAsync(ct);

        var rules = server.Ports
            .Select(p => new ForwardingRule(p.Name, p.Protocol, p.NodePort))
            .ToList();

        var firewallCommands = BuildFirewallCommands(serverName, server.Ports);

        var cgnat = DetectCgnat(publicIp);
        var note = cgnat.Detected
            ? cgnat.Detail + " Port forwarding won't help — use a tunnel (e.g. Tailscale) or ask your ISP for a public IP."
            : null;

        return new ForwardingGuide(serverName, lanAddresses, publicIp, rules, firewallCommands, note);
    }

    // --- external reachability classification ---

    private async Task<(ReachabilityState State, string? Detail)> ClassifyExternalAsync(
        bool isTcp, int port, string? publicIp, bool cgnat, bool locallyListening, CancellationToken ct)
    {
        if (!isTcp)
        {
            return (ReachabilityState.Unverified, "UDP reachability can't be verified from outside.");
        }
        if (cgnat)
        {
            return (ReachabilityState.Unverified, "Behind CGNAT — forwarding can't make this reachable.");
        }
        if (publicIp is null)
        {
            return (ReachabilityState.Unverified, "Public IP unknown (offline or lookup failed).");
        }
        if (!locallyListening)
        {
            return (ReachabilityState.Closed, "The server isn't listening on this port yet — start it first.");
        }

        var state = await _portChecker.CheckTcpAsync(publicIp, port, ct);
        var detail = state switch
        {
            ReachabilityState.Open => "Reachable from the internet.",
            ReachabilityState.Closed => "Not reachable — check the router forward and firewall for this port.",
            _ => "Couldn't verify from outside; the port is at least open on this machine.",
        };
        return (state, detail);
    }

    private static async Task<bool> IsLocallyListeningAsync(int port, CancellationToken ct)
    {
        if (port <= 0)
        {
            return false;
        }

        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(LocalDialTimeout);
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            return true; // published + accepted (docker-proxy) — the port is live on this machine
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Binds a host port for the length of an outside-in probe so an otherwise
    /// unused port still completes the TCP handshake (queued in the accept
    /// backlog — nothing has to accept for the probe to succeed). Yields null
    /// when the port is already taken, which is equally testable.
    /// </summary>
    private sealed class HeldPort : IDisposable
    {
        private readonly TcpListener _listener;

        private HeldPort(TcpListener listener) => _listener = listener;

        public static HeldPort? TryHold(int port)
        {
            var listener = new TcpListener(IPAddress.Any, port);
            try
            {
                listener.Start();
                return new HeldPort(listener);
            }
            catch (SocketException)
            {
                listener.Stop();
                return null;
            }
        }

        public void Dispose() => _listener.Stop();
    }

    // --- CGNAT / private-range detection ---

    /// <summary>
    /// Flags when the observed public IP can't actually accept forwarded traffic:
    /// it's in the CGNAT shared range (100.64.0.0/10) or an RFC1918 private range
    /// (a sign of double-NAT). Either way, router forwarding on the user's own
    /// router won't expose the server.
    /// </summary>
    internal static (bool Detected, string? Detail) DetectCgnat(string? publicIp)
    {
        if (publicIp is null || !IPAddress.TryParse(publicIp, out var ip) ||
            ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return (false, null);
        }

        var bytes = ip.GetAddressBytes();

        // 100.64.0.0/10 — carrier-grade NAT shared address space.
        if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
        {
            return (true, "Your ISP appears to use Carrier-Grade NAT (public IP is in 100.64.0.0/10).");
        }

        // RFC1918 private ranges appearing as the "public" IP ⇒ another NAT in front.
        var isPrivate =
            bytes[0] == 10 ||
            (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168);
        if (isPrivate)
        {
            return (true, "Your public IP is a private address, so there's another router/NAT in front of you.");
        }

        return (false, null);
    }

    // --- LAN addresses (shared with NetworkController) ---

    /// <summary>
    /// The host's real LAN IPv4 address(es), in interface order. A default gateway
    /// is what separates the real LAN adapter from virtual ones
    /// (Docker/WSL/Hyper-V vEthernet), whose addresses other machines can't reach.
    /// </summary>
    public static IReadOnlyList<string> GetLanAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic =>
                nic.OperationalStatus == OperationalStatus.Up &&
                nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .Select(nic => nic.GetIPProperties())
            .Where(props => props.GatewayAddresses.Count > 0)
            .SelectMany(props => props.UnicastAddresses)
            .Where(addr =>
                addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(addr.Address))
            .Select(addr => addr.Address.ToString())
            .Distinct()
            .ToList();

    private static IReadOnlyList<string> BuildFirewallCommands(string serverName, IReadOnlyList<PortMapping> ports)
    {
        var commands = new List<string>();

        foreach (var protocol in new[] { "TCP", "UDP" })
        {
            var matching = ports
                .Where(p => string.Equals(p.Protocol, protocol, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.NodePort)
                .Distinct()
                .OrderBy(p => p)
                .ToList();

            if (matching.Count == 0)
            {
                continue;
            }

            commands.Add(
                $"New-NetFirewallRule -DisplayName \"GameDashboard {serverName} {protocol}\" " +
                $"-Direction Inbound -Protocol {protocol} -LocalPort {string.Join(",", matching)} -Action Allow");
        }

        return commands;
    }
}
