using DoiQ.App.Services;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace DoiQ.App;

public sealed class PlatformNotifier : INotifier
{
    private bool _registered;

    public Task RequestPermission(CancellationToken ct)
    {
        // Windows needs no permission, only a registration (which an unpackaged app can do itself).
        if (!_registered && AppNotificationManager.IsSupported())
        {
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        return Task.CompletedTask;
    }

    public Task Show(string title, string body, CancellationToken ct)
    {
        if (_registered) AppNotificationManager.Default.Show(new AppNotificationBuilder().AddText(title).AddText(body).BuildNotification());
        return Task.CompletedTask;
    }
}
