using Android.App;
using Android.Content;
using AndroidX.Core.App;
using DoiQ.App.Services;

namespace DoiQ.App;

public sealed class PlatformNotifier : INotifier
{
    private const string ChannelId = "needs-you";
    private int _nextId = 1;

    public async Task RequestPermission(CancellationToken ct)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            await MainThread.InvokeOnMainThreadAsync(() => Permissions.RequestAsync<Permissions.PostNotifications>());
    }

    public Task Show(string title, string body, CancellationToken ct)
    {
        var context = Platform.AppContext;
        var manager = NotificationManagerCompat.From(context);
        if (!manager.AreNotificationsEnabled()) return Task.CompletedTask;

        if (OperatingSystem.IsAndroidVersionAtLeast(26) && context.GetSystemService(Context.NotificationService) is NotificationManager system)
            system.CreateNotificationChannel(new NotificationChannel(ChannelId, "Waiting on you", NotificationImportance.Default));

        var open = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
        var tap = open is null ? null : PendingIntent.GetActivity(context, 0, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var notification = new NotificationCompat.Builder(context, ChannelId)
            .SetSmallIcon(Resource.Mipmap.appicon)!
            .SetContentTitle(title)!
            .SetContentText(body)!
            .SetAutoCancel(true)!
            .SetContentIntent(tap)!
            .Build()!;
        manager.Notify(_nextId++, notification);
        return Task.CompletedTask;
    }
}
