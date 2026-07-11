namespace GameDashboard.Api.Configuration;

/// <summary>
/// Strongly-typed configuration bound to the "Dashboard" section of appsettings.json.
/// See design.md → Configuration.
/// </summary>
public sealed class DashboardOptions
{
    public const string SectionName = "Dashboard";

    /// <summary>Kubernetes namespace all game servers live in.</summary>
    public string Namespace { get; set; } = "game-servers";

    /// <summary>Name of the Kubernetes Secret holding tokens/RCON passwords.</summary>
    public string SecretName { get; set; } = "game-secrets";

    /// <summary>Address the backend binds to. Loopback by default (localhost-first).</summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>Port the backend listens on.</summary>
    public int Port { get; set; } = 5000;

    /// <summary>When the backend is exposed beyond loopback, require an auth token.</summary>
    public bool RequireAuthWhenExposed { get; set; } = true;

    /// <summary>
    /// Shared-secret token required on every request when the backend is bound to
    /// a non-loopback address and <see cref="RequireAuthWhenExposed"/> is true.
    /// Sent by clients via the "X-Api-Token" header. Not used at all when bound to
    /// loopback. See requirements.md → Req 14.1, Req 14.6.
    /// </summary>
    public string? ApiToken { get; set; }

    /// <summary>
    /// True if <see cref="BindAddress"/> resolves to a loopback address
    /// (127.0.0.1, ::1, etc.). Non-loopback binding is what triggers the
    /// token-auth requirement (Req 14.6).
    /// </summary>
    public bool IsLoopbackBind =>
        System.Net.IPAddress.TryParse(BindAddress, out var ip) && System.Net.IPAddress.IsLoopback(ip);

    /// <summary>
    /// Browser origins allowed to call the API cross-origin (CORS). Needed by the
    /// desktop frontend: the Tauri webview serves the UI from http://tauri.localhost
    /// and the Vite dev server from http://localhost:5173, both of which are
    /// cross-origin to this backend. CORS is origin gating for browsers only —
    /// token auth for non-loopback exposure is handled separately by
    /// TokenAuthMiddleware (Req 14).
    /// </summary>
    public IList<string> AllowedCorsOrigins { get; set; } = new List<string>
    {
        "http://tauri.localhost",
        "http://localhost:5173",
        "http://127.0.0.1:5173",
    };

    public AutoScaleOptions AutoScale { get; set; } = new();
    public MetricsOptions Metrics { get; set; } = new();
}

public sealed class AutoScaleOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 30;
    public int MemoryHighWaterPercent { get; set; } = 80;
}

public sealed class MetricsOptions
{
    public int PushIntervalSeconds { get; set; } = 5;
}
