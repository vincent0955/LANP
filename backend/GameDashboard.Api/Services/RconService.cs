using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoreRCON;
using Docker.DotNet;
using GameDashboard.Api.Exceptions;
using GameDashboard.Api.Models;
using GameDashboard.Api.Services.Docker;

namespace GameDashboard.Api.Services;

/// <summary>
/// See <see cref="IRconService"/>. Handles the protocol differences between Source
/// engine games (CS2, Insurgency) and Minecraft: both use the Source RCON wire
/// protocol, but the query command and response format for player info differ.
/// </summary>
public sealed partial class RconService : IRconService
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(3);

    private readonly IDockerClientFactory _clientFactory;
    private readonly ISecretsStore _secretsStore;
    private readonly ILogger<RconService> _logger;

    public RconService(
        IDockerClientFactory clientFactory,
        ISecretsStore secretsStore,
        ILogger<RconService> logger)
    {
        _clientFactory = clientFactory;
        _secretsStore = secretsStore;
        _logger = logger;
    }

    public async Task<PlayerInfo?> QueryPlayerInfoAsync(string serverName, CancellationToken ct)
    {
        try
        {
            var connection = await ResolveConnectionAsync(serverName, ct);
            if (connection is null)
            {
                return null; // no RCON port / password configured for this server
            }

            using var rcon = await ConnectAsync(connection, ct);
            if (rcon is null)
            {
                return null; // connection/auth failed — fail soft
            }

            var response = await SendWithTimeoutAsync(rcon, connection.QueryCommand, ct);
            return response is null ? null : ParsePlayerInfo(connection.Engine, response);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "QueryPlayerInfoAsync failed for {ServerName}; reporting unavailable.", serverName);
            return null;
        }
    }

    public async Task<string> SendCommandAsync(string serverName, string command, CancellationToken ct)
    {
        var connection = await ResolveConnectionAsync(serverName, ct)
            ?? throw new RconUnavailableException(serverName, "RCON is not configured for this server.");

        using var rcon = await ConnectAsync(connection, ct)
            ?? throw new RconUnavailableException(serverName, "Failed to connect or authenticate.");

        var response = await SendWithTimeoutAsync(rcon, command, ct);
        return response ?? throw new RconUnavailableException(serverName, "Command timed out.");
    }

    // --- connection resolution ---

    private enum Engine { Source, Minecraft }

    private sealed record RconConnection(IPAddress Host, ushort Port, string Password, string QueryCommand, Engine Engine);

    /// <summary>
    /// Resolves the RCON endpoint for a server: host port from the container's
    /// ports label, password from the local secrets store. Returns null (not an
    /// exception) if any part is missing, so callers can fail soft.
    /// </summary>
    private async Task<RconConnection?> ResolveConnectionAsync(string serverName, CancellationToken ct)
    {
        var (client, _) = await _clientFactory.TryGetClientAsync(ct);
        if (client is null)
        {
            return null;
        }

        try
        {
            var container = await client.Containers.InspectContainerAsync(serverName, ct);
            var labels = container.Config?.Labels;
            if (labels is null ||
                !labels.TryGetValue(ContainerLabels.Managed, out var managed) ||
                managed != ContainerLabels.ManagedValue)
            {
                return null;
            }

            var ports = PortsOf(labels);

            // Prefer a port explicitly named "rcon" (Source templates name their
            // TCP 27015 RCON channel this way since 2026-07-11). The fallback keeps
            // RCON working for servers deployed before the rename, whose Source TCP
            // port is still named "game-tcp".
            var rconPort = ports.FirstOrDefault(p => p.Name == "rcon")?.NodePort
                ?? ports.FirstOrDefault(p => p.Name is "game-tcp" or "game")?.NodePort;
            if (rconPort is null or 0)
            {
                return null;
            }

            var (engine, queryCommand, secretKey) = ClassifyEngine(container.Config?.Image ?? "");

            // RCON passwords are per-server secrets, stored under the server's scope.
            var password = await _secretsStore.GetValueAsync(
                ServerSecretKey.Scope(serverName, secretKey), ct);
            if (password is null)
            {
                return null;
            }

            // Published ports are reachable via localhost in both WSL NAT and
            // mirrored networking modes.
            return new RconConnection(IPAddress.Loopback, (ushort)rconPort.Value, password, queryCommand, engine);
        }
        catch (DockerContainerNotFoundException)
        {
            return null;
        }
        catch (DockerApiException)
        {
            return null;
        }
    }

    private static IReadOnlyList<PortMapping> PortsOf(IDictionary<string, string> labels)
    {
        if (!labels.TryGetValue(ContainerLabels.Ports, out var json))
        {
            return Array.Empty<PortMapping>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<PortMapping>>(json)
                ?? (IReadOnlyList<PortMapping>)Array.Empty<PortMapping>();
        }
        catch (JsonException)
        {
            return Array.Empty<PortMapping>();
        }
    }

    /// <summary>
    /// Determines which RCON query command/protocol dialect to use based on the
    /// container image. Curated templates only for now (catalog games without
    /// RCON support simply resolve to null upstream).
    /// </summary>
    private static (Engine Engine, string QueryCommand, string SecretKey) ClassifyEngine(string image)
    {
        if (image.Contains("minecraft-server", StringComparison.OrdinalIgnoreCase))
        {
            return (Engine.Minecraft, "list", "RCON_PASSWORD");
        }

        // CS2, Insurgency, and other Source-engine LinuxGSM images.
        return (Engine.Source, "status", "CS2_RCONPW");
    }

    private async Task<RCON?> ConnectAsync(RconConnection connection, CancellationToken ct)
    {
        try
        {
            var rcon = new RCON(connection.Host, connection.Port, connection.Password);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ConnectTimeout);

            await rcon.ConnectAsync().WaitAsync(cts.Token);
            return rcon;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RCON connect/auth failed on {Host}:{Port}.", connection.Host, connection.Port);
            return null;
        }
    }

    private static async Task<string?> SendWithTimeoutAsync(RCON rcon, string command, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(CommandTimeout);
            return await rcon.SendCommandAsync(command, CommandTimeout).WaitAsync(cts.Token);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // --- response parsing ---

    private static PlayerInfo? ParsePlayerInfo(Engine engine, string response) => engine switch
    {
        Engine.Source => ParseSourceStatus(response),
        Engine.Minecraft => ParseMinecraftList(response),
        _ => null
    };

    /// <summary>
    /// Parses Source engine "status" output. Relevant lines look like:
    ///   players     : 3 humans, 0 bots (16 max)
    ///   map         : de_dust2
    /// </summary>
    private static PlayerInfo? ParseSourceStatus(string response)
    {
        var playersMatch = SourcePlayersRegex().Match(response);
        var mapMatch = SourceMapRegex().Match(response);

        if (!playersMatch.Success)
        {
            return null;
        }

        var current = int.Parse(playersMatch.Groups["current"].Value);
        var max = int.Parse(playersMatch.Groups["max"].Value);
        var map = mapMatch.Success ? mapMatch.Groups["map"].Value.Trim() : null;

        return new PlayerInfo(current, max, map);
    }

    /// <summary>
    /// Parses Minecraft "list" output:
    ///   "There are 2 of a max of 10 players online: Alice, Bob"
    /// </summary>
    private static PlayerInfo? ParseMinecraftList(string response)
    {
        var match = MinecraftListRegex().Match(response);
        if (!match.Success)
        {
            return null;
        }

        var current = int.Parse(match.Groups["current"].Value);
        var max = int.Parse(match.Groups["max"].Value);
        return new PlayerInfo(current, max, CurrentMap: null); // vanilla "list" has no map info
    }

    [GeneratedRegex(@"players\s*:\s*(?<current>\d+)\s+humans.*\((?<max>\d+)\s+max\)")]
    private static partial Regex SourcePlayersRegex();

    [GeneratedRegex(@"map\s*:\s*(?<map>\S+)")]
    private static partial Regex SourceMapRegex();

    [GeneratedRegex(@"There are (?<current>\d+) of a max(?:imum)? of (?<max>\d+) players online")]
    private static partial Regex MinecraftListRegex();
}
