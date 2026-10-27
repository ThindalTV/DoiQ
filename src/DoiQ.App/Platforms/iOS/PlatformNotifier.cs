using DoiQ.App.Services;
using Foundation;
using UserNotifications;

namespace DoiQ.App;

public sealed class PlatformNotifier : INotifier
{
    public async Task RequestPermission(CancellationToken ct) =>
        await UNUserNotificationCenter.Current.RequestAuthorizationAsync(UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound);

    public async Task Show(string title, string body, CancellationToken ct)
    {
        var content = new UNMutableNotificationContent { Title = title, Body = body, Sound = UNNotificationSound.Default };
        var request = UNNotificationRequest.FromIdentifier(new NSUuid().AsString(), content, trigger: null);
        await UNUserNotificationCenter.Current.AddNotificationRequestAsync(request);
    }
}
