using System.Net;
using System.Text.RegularExpressions;
using CoreRCON;
using GameDashboard.Api.Configuration;
using GameDashboard.Api.Exceptions;
using GameDashboard.Api.Models;
using k8s;
using k8s.Autorest;
using Microsoft.Extensions.Options;

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

    private readonly IKubernetesClientFactory _clientFactory;
    private readonly DashboardOptions _options;
    private readonly ILogger<RconService> _logger;

    public RconService(
        IKubernetesClientFactory clientFactory,
        IOptions<DashboardOptions> options,
        ILogger<RconService> logger)
    {
        _clientFactory = clientFactory;
        _options = options.Value;
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
                return null; // connection/auth failed — fail soft (Req 10.3)
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
    /// Resolves the RCON endpoint for a server: NodePort from its Service, password
    /// from the game-secrets Secret. Returns null (not an exception) if any part is
    /// missing, so callers can fail soft.
    /// </summary>
    private async Task<RconConnection?> ResolveConnectionAsync(string serverName, CancellationToken ct)
    {
        if (!_clientFactory.TryGetClient(out var client, out _))
        {
            return null;
        }

        try
        {
            var deployment = await client!.AppsV1.ReadNamespacedDeploymentAsync(
                serverName, _options.Namespace, cancellationToken: ct);

            var appLabel = deployment.Spec?.Selector?.MatchLabels?.TryGetValue("app", out var lbl) == true
                ? lbl : serverName;

            var services = await client.CoreV1.ListNamespacedServiceAsync(_options.Namespace, cancellationToken: ct);
            var service = services.Items.FirstOrDefault(s =>
                s.Spec?.Selector != null &&
                s.Spec.Selector.TryGetValue("app", out var v) && v == appLabel);

            // Prefer a port explicitly named "rcon" (Source templates name their
            // TCP 27015 RCON channel this way since 2026-07-11). The fallback keeps
            // RCON working for servers deployed before the rename, whose Source TCP
            // port is still named "game-tcp".
            var rconPort = service?.Spec?.Ports?.FirstOrDefault(p => p.Name == "rcon")?.NodePort
                ?? service?.Spec?.Ports?.FirstOrDefault(p => p.Name is "game-tcp" or "game")?.NodePort;
            if (rconPort is null)
            {
                return null;
            }

            var (engine, queryCommand, secretKey) = ClassifyEngine(deployment);

            var secret = await client.CoreV1.ReadNamespacedSecretAsync(
                _options.SecretName, _options.Namespace, cancellationToken: ct);
            if (secret.Data is null || !secret.Data.TryGetValue(secretKey, out var passwordBytes))
            {
                return null;
            }

            var password = System.Text.Encoding.UTF8.GetString(passwordBytes);

            // NodePort is reachable via localhost on a single-node Docker Desktop
            // cluster (design.md: single-node assumption).
            return new RconConnection(IPAddress.Loopback, (ushort)rconPort.Value, password, queryCommand, engine);
        }
        catch (HttpOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Determines which RCON query command/protocol dialect to use based on the
    /// container image. Curated templates only for now (Phase 7 catalog games
    /// without RCON support will simply resolve to null upstream).
    /// </summary>
    private static (Engine Engine, string QueryCommand, string SecretKey) ClassifyEngine(k8s.Models.V1Deployment deployment)
    {
        var image = deployment.Spec?.Template.Spec?.Containers?.FirstOrDefault()?.Image ?? "";

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
