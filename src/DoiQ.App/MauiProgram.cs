using DoiQ.App.Components;
using DoiQ.App.Core;
using DoiQ.App.Services;
using DoiQ.Client.Services;
using DoiQ.Shared.Dtos;
using Microsoft.Extensions.Logging;

namespace DoiQ.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddMauiBlazorWebView();

        // Networking: every request goes to the configured server through the offline cache, with cookies kept on disk.
        builder.Services.AddDoiQAppCore(FileSystem.AppDataDirectory);
        builder.Services.AddSingleton<IOfflineStatus, OfflineStatus>();
        builder.Services.AddSingleton(sp =>
        {
            var server = sp.GetRequiredService<ServerSettings>();
            var jar = sp.GetRequiredService<PersistentCookieJar>();
            return new ClientOptions
            {
                Administration = false,
                HubUrl = () => server.Resolve(BoardHubEvents.HubPath) ?? throw new InvalidOperationException("No server configured."),
                ConfigureHub = o => o.Cookies = jar.Container,
                HostSection = typeof(ServerSection),
            };
        });

        // The shared UI's services, as the browser client registers them; one set per page (a new page on a server change).
        builder.Services.AddScoped<IDoiQApiClient>(sp => new DoiQApiClient(sp.GetRequiredService<HttpClient>()));
        builder.Services.AddScoped<AuthState>();
        builder.Services.AddScoped<NoticeService>();
        builder.Services.AddScoped<BoardHubClient>();

        builder.Services.AddSingleton<AppLifecycle>();
        builder.Services.AddSingleton<INotifier, PlatformNotifier>();
        builder.Services.AddScoped<NeedsYouNotifier>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
