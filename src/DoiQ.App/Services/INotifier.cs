namespace DoiQ.App.Services;

/// <summary>Local notifications, one implementation per platform (Platforms/*/PlatformNotifier.cs).</summary>
public interface INotifier
{
    /// <summary>Asks for permission where the platform wants that (Android 13+, iOS). Safe to call again.</summary>
    Task RequestPermission(CancellationToken ct);

    Task Show(string title, string body, CancellationToken ct);
}
