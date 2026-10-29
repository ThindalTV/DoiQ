using Microsoft.Extensions.DependencyInjection;

namespace DoiQ.App.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// BaseAddress of the app's <see cref="HttpClient"/>. It is only a placeholder: <see cref="OfflineCacheHandler"/>
    /// swaps in the configured server per request, so changing server needs no new client.
    /// </summary>
    public const string PlaceholderBase = "https://doiq.app/";

    /// <summary>
    /// Registers <see cref="ServerSettings"/>, <see cref="PersistentCookieJar"/> (loaded), <see cref="ConnectivityState"/>,
    /// <see cref="OfflineCacheOptions"/>, <see cref="OfflineCacheHandler"/> and a singleton <see cref="HttpClient"/>.
    /// SignalR: use <c>jar.Container</c> as the connection's cookie container and
    /// <c>settings.Resolve("hubs/...")</c> for the URL; sharing the jar shares the login.
    /// </summary>
    public static IServiceCollection AddDoiQAppCore(this IServiceCollection services, string dataDirectory)
    {
        services.AddSingleton(new ServerSettings(dataDirectory));
        services.AddSingleton(_ =>
        {
            var jar = new PersistentCookieJar(dataDirectory);
            jar.Load();
            return jar;
        });
        services.AddSingleton<ConnectivityState>();
        services.AddSingleton(new OfflineCacheOptions());
        services.AddSingleton(sp => new OfflineCacheHandler(
            sp.GetRequiredService<ServerSettings>(), sp.GetRequiredService<ConnectivityState>(),
            Path.Combine(dataDirectory, "cache"), sp.GetRequiredService<PersistentCookieJar>(),
            sp.GetRequiredService<OfflineCacheOptions>()));
        services.AddSingleton(sp =>
        {
            var handler = sp.GetRequiredService<OfflineCacheHandler>();
            handler.InnerHandler = new SocketsHttpHandler
            {
                CookieContainer = sp.GetRequiredService<PersistentCookieJar>().Container,
                UseCookies = true,
            };
            // The handler owns the (short) network timeout; HttpClient's own 100 s would cut long loads.
            return new HttpClient(handler) { BaseAddress = new Uri(PlaceholderBase), Timeout = Timeout.InfiniteTimeSpan };
        });
        return services;
    }
}
