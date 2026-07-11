using System.Net;

namespace GameDashboard.Api.Services;

public interface IPublicIpService
{
    /// <summary>The machine's public IPv4 as seen from the internet, or null when
    /// it can't be determined (offline, lookup service down).</summary>
    Task<string?> GetPublicIpAsync(CancellationToken ct);
}

/// <summary>
/// Resolves the public IP via api.ipify.org (plain-text IPv4 echo). The result is
/// cached — successes for 10 minutes, failures for 1 — so browsing server pages
/// never hammers an external service, and a lookup failure only ever means a
/// missing field in the UI, never an error.
/// </summary>
public sealed class PublicIpService : IPublicIpService
{
    private static readonly TimeSpan SuccessTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailureTtl = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(4);
    private const string LookupUrl = "https://api.ipify.org";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PublicIpService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string? _cached;
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;
    private bool _lastFetchFailed;

    public PublicIpService(IHttpClientFactory httpClientFactory, ILogger<PublicIpService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string?> GetPublicIpAsync(CancellationToken ct)
    {
        if (IsCacheFresh())
        {
            return _cached;
        }

        await _lock.WaitAsync(ct);
        try
        {
            if (IsCacheFresh())
            {
                return _cached;
            }

            _cached = await FetchAsync(ct);
            _lastFetchFailed = _cached is null;
            _fetchedAt = DateTimeOffset.UtcNow;
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    private bool IsCacheFresh()
    {
        var ttl = _lastFetchFailed ? FailureTtl : SuccessTtl;
        return DateTimeOffset.UtcNow - _fetchedAt < ttl;
    }

    private async Task<string?> FetchAsync(CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(LookupTimeout);
            var client = _httpClientFactory.CreateClient(nameof(PublicIpService));
            var body = (await client.GetStringAsync(LookupUrl, timeoutCts.Token)).Trim();
            return IPAddress.TryParse(body, out var ip) &&
                   ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? body
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Public IP lookup failed.");
            return null;
        }
    }
}
