using GameDashboard.Api.Configuration;
using GameDashboard.Api.Hubs;
using GameDashboard.Api.Middleware;
using GameDashboard.Api.RealTime;
using GameDashboard.Api.Services;
using GameDashboard.Api.Services.Docker;
using GameDashboard.Api.Services.Runtime;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration (options pattern) ---
builder.Services
    .AddOptions<DashboardOptions>()
    .Bind(builder.Configuration.GetSection(DashboardOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Resolve options early so we can configure Kestrel's bind address/port.
var dashboardOptions = builder.Configuration
    .GetSection(DashboardOptions.SectionName)
    .Get<DashboardOptions>() ?? new DashboardOptions();

// --- Kestrel: bind to loopback by default (localhost-first, Req 14) ---
builder.WebHost.ConfigureKestrel(kestrel =>
{
    if (System.Net.IPAddress.TryParse(dashboardOptions.BindAddress, out var ip))
    {
        kestrel.Listen(ip, dashboardOptions.Port);
    }
    else
    {
        // Fallback to loopback if the configured address is invalid.
        kestrel.ListenLocalhost(dashboardOptions.Port);
    }
});

// Fail fast rather than silently running unauthenticated: if the operator has
// configured non-loopback exposure with auth required, a token MUST be set
// (Req 14.1, Req 14.6).
if (!dashboardOptions.IsLoopbackBind && dashboardOptions.RequireAuthWhenExposed &&
    string.IsNullOrWhiteSpace(dashboardOptions.ApiToken))
{
    throw new InvalidOperationException(
        $"Dashboard is configured to bind to a non-loopback address ({dashboardOptions.BindAddress}) " +
        "with RequireAuthWhenExposed=true, but no Dashboard:ApiToken is configured. " +
        "Set Dashboard:ApiToken in configuration, or set RequireAuthWhenExposed=false to explicitly " +
        "run unauthenticated (not recommended).");
}

builder.Services.AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter()));

// --- CORS for the desktop frontend ---
// The Tauri webview (http://tauri.localhost) and Vite dev server are cross-origin
// to this backend, so both plain fetches and the SignalR /negotiate handshake need
// CORS. AllowCredentials + explicit origins (never AllowAnyOrigin) because the
// SignalR JavaScript client sends credentialed requests.
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(dashboardOptions.AllowedCorsOrigins.ToArray())
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()));

// --- Docker Engine connectivity (docs/docker-migration.md) ---
// Registered before the client factory, which consults it to pick an endpoint.
builder.Services.AddSingleton<IRuntimeModeStore, RuntimeModeStore>();
builder.Services.AddSingleton<IDockerClientFactory, DockerClientFactory>();
builder.Services.AddScoped<IServerOrchestrator, DockerService>();
builder.Services.AddSingleton<IContainerSpecBuilder, ContainerSpecBuilder>();
builder.Services.AddSingleton<DeployTracker>();
builder.Services.AddSingleton<ISecretsStore, FileSecretsStore>();
builder.Services.AddSingleton<ILastActiveStore, LastActiveStore>();
builder.Services.AddSingleton<ITcpReadinessProber, TcpReadinessProber>();
builder.Services.AddSingleton<IGameCatalogService, GameCatalogService>();

// --- Bundled runtime first-run flow (docs/docker-migration.md → Part 2) ---
builder.Services.AddSingleton<IWslRunner, WslRunner>();
builder.Services.AddSingleton<IRuntimeSetupService, RuntimeSetupService>();
builder.Services.AddHostedService<RuntimeAutoStartService>();

// --- Real-time (Phase 5) ---
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
        options.PayloadSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddSingleton<ILogStreamManager, LogStreamManager>();
builder.Services.AddHostedService<ContainerWatchService>();
builder.Services.AddHostedService<DownloadProgressService>();

// --- Metrics (Phase 6) ---
builder.Services.AddSingleton<IMetricsService, DockerMetricsService>();
builder.Services.AddHostedService<MetricsPushService>();

// --- RCON (Phase 6) ---
builder.Services.AddSingleton<IRconService, RconService>();

// --- World backups (WS1): game-agnostic volume snapshots + scheduled auto-backups ---
builder.Services.AddSingleton<IBackupService, BackupService>();
builder.Services.AddSingleton<IBackupSettingsStore, BackupSettingsStore>();
builder.Services.AddHostedService<BackupSchedulerService>();

// --- Join addresses (public IP lookup, cached) ---
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IPublicIpService, PublicIpService>();

// --- Network diagnostics (WS2): CGNAT detection, reachability probe, forwarding guide ---
builder.Services.AddHttpClient(nameof(CheckHostPortChecker), client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "game-dashboard/1.0 (github.com/vincent0955/source-server-cluster)");
});
builder.Services.AddSingleton<IExternalPortChecker, CheckHostPortChecker>();
builder.Services.AddScoped<INetworkDiagnosticsService, NetworkDiagnosticsService>();

// --- Minecraft deploy metadata (versions + Modrinth search, all keyless) ---
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<IMinecraftMetadataService, MinecraftMetadataService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    // Modrinth's ToS requires a descriptive User-Agent identifying the project.
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "game-dashboard/1.0 (github.com/vincent0955/source-server-cluster)");
});

// --- Auto-scaling (Phase 8) ---
builder.Services.AddHostedService<AutoScaleService>();

var app = builder.Build();

// (The k8s-era namespace bootstrap is gone: there is nothing to pre-create on
// a Docker engine — volumes are created per deploy, idempotently.)

// --- Global exception handling → ProblemDetails (Req 14) ---
app.UseMiddleware<ExceptionHandlingMiddleware>();

// CORS must run before token auth: preflight OPTIONS requests cannot carry the
// X-Api-Token header (browsers strip custom headers from preflights), so they
// must be answered by the CORS middleware, never rejected with a 401.
app.UseCors();

app.UseMiddleware<TokenAuthMiddleware>();

// Root + health endpoints (full cluster health arrives in Phase 2).
app.MapGet("/", () => Results.Ok(new { service = "GameDashboard.Api", status = "ok" }));

app.MapControllers();
app.MapHub<DashboardHub>("/hubs/dashboard");

app.Run();

// Exposed for integration testing via WebApplicationFactory.
public partial class Program { }
