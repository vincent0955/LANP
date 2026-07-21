using System.Text.Json;
using GameDashboard.Api.Configuration;
using GameDashboard.Api.Models;
using Microsoft.Extensions.Options;

namespace GameDashboard.Api.Services;

/// <summary>
/// Performs a genuine outside-in TCP reachability check — a third party must
/// initiate the connection, which the backend (behind the same NAT) cannot do
/// itself. Keyless by design; any failure yields <see cref="ReachabilityState.Unverified"/>
/// so a checker outage never surfaces as an error.
/// </summary>
public interface IExternalPortChecker
{
    Task<ReachabilityState> CheckTcpAsync(string publicIp, int port, CancellationToken ct);
}

/// <summary>
/// Uses check-host.net's keyless JSON API: POST-less GET /check-tcp to enqueue a
/// check from several public nodes, then poll /check-result for their verdicts.
/// A single reachable node ⇒ Open; all nodes failing ⇒ Closed; nothing resolved
/// in the time budget ⇒ Unverified.
/// </summary>
public sealed class CheckHostPortChecker : IExternalPortChecker
{
    private static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(700);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly NetworkOptions _options;
    private readonly ILogger<CheckHostPortChecker> _logger;

    public CheckHostPortChecker(
        IHttpClientFactory httpClientFactory,
        IOptions<DashboardOptions> options,
        ILogger<CheckHostPortChecker> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value.Network;
        _logger = logger;
    }

    public async Task<ReachabilityState> CheckTcpAsync(string publicIp, int port, CancellationToken ct)
    {
        if (!_options.ReachabilityProbeEnabled || string.IsNullOrWhiteSpace(_options.PortCheckBaseUrl))
        {
            return ReachabilityState.Unverified;
        }

        try
        {
            using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budgetCts.CancelAfter(TotalBudget);
            return await RunCheckAsync(publicIp, port, budgetCts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "External port check for {Ip}:{Port} failed; reporting unverified.", publicIp, port);
            return ReachabilityState.Unverified;
        }
    }

    private async Task<ReachabilityState> RunCheckAsync(string publicIp, int port, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(nameof(CheckHostPortChecker));
        var baseUrl = _options.PortCheckBaseUrl.TrimEnd('/');

        using var enqueue = new HttpRequestMessage(
            HttpMethod.Get, $"{baseUrl}/check-tcp?host={publicIp}:{port}&max_nodes=3");
        enqueue.Headers.Accept.ParseAdd("application/json");

        using var enqueueResponse = await client.SendAsync(enqueue, ct);
        enqueueResponse.EnsureSuccessStatusCode();

        using var enqueueDoc = JsonDocument.Parse(await enqueueResponse.Content.ReadAsStringAsync(ct));
        if (!enqueueDoc.RootElement.TryGetProperty("request_id", out var idElement) ||
            idElement.GetString() is not { Length: > 0 } requestId)
        {
            return ReachabilityState.Unverified;
        }

        // Results populate asynchronously node-by-node; poll until the budget runs out.
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(PollInterval, ct);

            using var resultResponse = await client.GetAsync($"{baseUrl}/check-result/{requestId}", ct);
            if (!resultResponse.IsSuccessStatusCode)
            {
                continue;
            }

            using var resultDoc = JsonDocument.Parse(await resultResponse.Content.ReadAsStringAsync(ct));
            var verdict = Interpret(resultDoc.RootElement);
            if (verdict is not null)
            {
                return verdict.Value;
            }
        }

        return ReachabilityState.Unverified;
    }

    /// <summary>
    /// Interprets a check-result document. Each node maps to an array whose first
    /// element is <c>{"time": …}</c> on success or <c>{"error": …}</c> on failure,
    /// or null while still pending. Returns Open on the first success, Closed once
    /// every node has reported and all failed, or null if any node is still pending.
    /// </summary>
    private static ReachabilityState? Interpret(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var anyPending = false;
        var anyReported = false;

        foreach (var node in root.EnumerateObject())
        {
            if (node.Value.ValueKind == JsonValueKind.Null)
            {
                anyPending = true;
                continue;
            }
            if (node.Value.ValueKind != JsonValueKind.Array || node.Value.GetArrayLength() == 0)
            {
                continue;
            }

            var first = node.Value[0];
            if (first.ValueKind == JsonValueKind.Object)
            {
                anyReported = true;
                if (first.TryGetProperty("time", out _) && !first.TryGetProperty("error", out _))
                {
                    return ReachabilityState.Open;
                }
            }
        }

        if (anyPending)
        {
            return null; // wait for the remaining nodes
        }
        return anyReported ? ReachabilityState.Closed : null;
    }
}
