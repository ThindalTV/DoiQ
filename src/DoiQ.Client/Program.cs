using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using DoiQ.Client;
using DoiQ.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Open the app with ?fake to run the whole client in memory (no server); otherwise everything goes to the real server.
builder.Services.AddScoped<FakeDoiQApiClient>();
builder.Services.AddScoped<IDoiQApiClient>(sp =>
    new Uri(sp.GetRequiredService<NavigationManager>().Uri).Query.Contains("fake", StringComparison.OrdinalIgnoreCase)
        ? sp.GetRequiredService<FakeDoiQApiClient>()
        : new DoiQApiClient(sp.GetRequiredService<HttpClient>()));
builder.Services.AddScoped<AuthState>();
builder.Services.AddScoped<NoticeService>();
builder.Services.AddScoped<BoardHubClient>();

await builder.Build().RunAsync();
