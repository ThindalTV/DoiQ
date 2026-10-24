using DoiQ.Core;
using DoiQ.Core.Services;
using DoiQ.Server.Auth;
using DoiQ.Server.Endpoints;
using DoiQ.Server.Hubs;
using DoiQ.Server.Outbox;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDoiQCore(builder.Configuration);

// Data Protection keys sign the login cookie. Kept next to the SQLite file (or at DataProtection:KeysPath) so a
// recreated container does not log everyone out. Not encrypted at rest: there is no certificate in this setup.
var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (string.IsNullOrWhiteSpace(keysPath))
{
    var dataSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(DoiQ.Core.DependencyInjection.ResolveConnectionString(builder.Configuration)).DataSource;
    keysPath = Path.Combine(string.IsNullOrEmpty(dataSource) || dataSource == ":memory:" ? Path.Combine(AppContext.BaseDirectory, "data") : Path.GetDirectoryName(Path.GetFullPath(dataSource))!, "keys");
}
Directory.CreateDirectory(keysPath);
builder.Services.AddDataProtection().SetApplicationName("DoiQ").PersistKeysToFileSystem(new DirectoryInfo(keysPath));

// Nightly SQLite backup (VACUUM INTO) next to the database, with a copy of the keys; see BackupOptions.
builder.Services.PostConfigure<DoiQ.Core.Backup.BackupOptions>(o => o.KeysPath ??= keysPath);
builder.Services.AddHostedService<DoiQ.Server.Backup.BackupWorker>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentActor, HttpCurrentActor>();
builder.Services.AddSingleton<IBoardBroadcaster, SignalRBroadcaster>();
builder.Services.AddDoiQOutbox(builder.Configuration);
builder.Services.AddSignalR();
builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)   // the agent is identified per request by its API key
    .WithTools<DoiQ.Server.Mcp.DoiQTools>();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddProblemDetails();

// Login and first-run setup: a fixed window per client IP, nothing queued, 429 + Retry-After beyond it.
builder.Services.Configure<LoginLimitOptions>(builder.Configuration.GetSection(LoginLimitOptions.Section));
builder.Services.AddSingleton<PasswordVerifyGate>();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(LoginLimitOptions.PolicyName, ctx =>
    {
        var limit = ctx.RequestServices.GetRequiredService<IOptions<LoginLimitOptions>>().Value;
        return RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, limit.PermitLimit),
                Window = TimeSpan.FromSeconds(Math.Max(1, limit.WindowSeconds)),
                QueueLimit = 0,
            });
    });
    o.OnRejected = async (rejected, ct) =>
    {
        var window = rejected.HttpContext.RequestServices.GetRequiredService<IOptions<LoginLimitOptions>>().Value.WindowSeconds;
        var retryAfter = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? (int)Math.Ceiling(wait.TotalSeconds) : window;
        rejected.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        rejected.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await rejected.HttpContext.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too many attempts",
            Detail = $"Too many login attempts. Try again in {retryAfter} seconds.",
        }, ct);
    };
});

// Behind a TLS-terminating reverse proxy: believe X-Forwarded-Proto / -For only from the configured proxies.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    var proxy = new ProxyOptions();
    builder.Configuration.GetSection(ProxyOptions.Section).Bind(proxy);
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var address in proxy.KnownProxies) o.KnownProxies.Add(IPAddress.Parse(address));
    foreach (var network in proxy.KnownNetworks)
    {
        var parts = network.Split('/');
        o.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse(parts[0]), int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)));
    }
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "doiq.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;   // Secure when the original request was HTTPS (see UseForwardedHeaders); plain HTTP on the LAN keeps working
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
        // APIs answer 401/403, never redirect to a login page.
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    })
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthHandler>(ActorClaims.ApiKeyScheme, _ => { });
builder.Services.AddAuthorization(o =>
{
    o.FallbackPolicy = new AuthorizationPolicyBuilder(CookieAuthenticationDefaults.AuthenticationScheme, ActorClaims.ApiKeyScheme)
        .RequireAuthenticatedUser().Build();
    o.AddPolicy(Policies.UserOnly, p => p
        .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme, ActorClaims.ApiKeyScheme)
        .RequireAuthenticatedUser().RequireClaim("actor_kind", "Human"));
});

var app = builder.Build();

await app.Services.InitializeDoiQ(app.Configuration, app.Lifetime.ApplicationStopping);

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// The Blazor client's files, fingerprinted and pre-compressed at publish; public so the login page can load.
app.MapStaticAssets().AllowAnonymous();
app.MapDoiQApi();
app.MapMcp("/mcp");
app.MapHub<BoardHub>(DoiQ.Shared.Dtos.BoardHubEvents.HubPath);
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

public partial class Program;
