using Bunit;
using DoiQ.Client.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace DoiQ.Client.Tests;

/// <summary>
/// Base for every component test: the services the client registers in Program.cs (against the in-memory
/// <see cref="FakeDoiQApiClient"/>), loose JS interop (the doiq.* helpers are not loaded in tests) and a test-owned
/// cancellation token with a timeout.
/// </summary>
public abstract class ClientTestBase : BunitContext, IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>xUnit calls this before Dispose: services that are only async-disposable (BoardHubClient) need it.</summary>
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(60));

    protected ClientTestBase()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<FakeDoiQApiClient>();
        Services.AddSingleton<IDoiQApiClient>(sp => sp.GetRequiredService<FakeDoiQApiClient>());
        Services.AddSingleton<NoticeService>();
        Services.AddSingleton<AuthState>();
    }

    /// <summary>Cancelled after a minute, so a hung test fails instead of blocking the run.</summary>
    protected CancellationToken Ct => _cts.Token;

    protected FakeDoiQApiClient Api => Services.GetRequiredService<FakeDoiQApiClient>();

    protected NoticeService Notices => Services.GetRequiredService<NoticeService>();

    /// <summary>A callback that records what it was called with, for the component's EventCallback parameters.</summary>
    protected EventCallback<T> Record<T>(List<T> calls) => EventCallback.Factory.Create<T>(this, calls.Add);

    protected EventCallback Count(Action onCall) => EventCallback.Factory.Create(this, onCall);

    /// <summary>
    /// True when a state attribute (aria-pressed, aria-checked, aria-expanded) is on. Blazor renders a bool-valued attribute
    /// as a bare attribute for true and leaves it out for false, so presence counts as on; an explicit "false" is off.
    /// </summary>
    protected static bool IsOn(AngleSharp.Dom.IElement element, string attribute) =>
        element.HasAttribute(attribute) && element.GetAttribute(attribute) != "false";

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cts.Dispose();
        base.Dispose(disposing);
    }
}
