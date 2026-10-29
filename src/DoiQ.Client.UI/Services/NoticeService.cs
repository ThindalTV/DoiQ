using System.Net;

namespace DoiQ.Client.Services;

public enum NoticeLevel { Info, Warning, Error }

/// <summary>One toast. Action is an optional button ("Retry", "Restore").</summary>
public sealed record Notice(int Id, NoticeLevel Level, string Text, string? ActionLabel = null, Func<Task>? Action = null);

/// <summary>
/// The app's toasts. Info and warnings dismiss themselves; errors stay until dismissed. <see cref="TryAsync"/> is how
/// user actions call the API: a failure becomes a notice instead of an unhandled exception.
/// </summary>
public sealed class NoticeService
{
    private static readonly TimeSpan InfoFor = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan WarningFor = TimeSpan.FromSeconds(12);

    private readonly List<Notice> _items = [];
    private int _next = 1;

    public IReadOnlyList<Notice> Items => _items;
    public event Action? Changed;

    public int Show(NoticeLevel level, string text, string? actionLabel = null, Func<Task>? action = null)
    {
        // The same message twice in a row (a retry that fails again) replaces the old one instead of piling up.
        _items.RemoveAll(n => n.Level == level && n.Text == text);
        var id = _next++;
        _items.Add(new Notice(id, level, text, actionLabel, action));
        Changed?.Invoke();
        if (level != NoticeLevel.Error) _ = ExpireAsync(id, level == NoticeLevel.Info ? InfoFor : WarningFor);
        return id;
    }

    public void Info(string text) => Show(NoticeLevel.Info, text);

    public void Dismiss(int id)
    {
        if (_items.RemoveAll(n => n.Id == id) > 0) Changed?.Invoke();
    }

    private async Task ExpireAsync(int id, TimeSpan after)
    {
        await Task.Delay(after);
        Dismiss(id);
    }

    /// <summary>Runs a user action. Null = it worked; otherwise the failure, already shown as a notice (409 = a warning, the rest errors).</summary>
    public async Task<Exception?> TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception e)
        {
            ShowFailure(e);
            return e;
        }
    }

    public void ShowFailure(Exception e)
    {
        var (level, text) = Describe(e);
        if (text.Length > 0) Show(level, text);
    }

    /// <summary>What a failed write says to the user. Empty text = say nothing (a 401 sends the user to the login page by itself).</summary>
    public static (NoticeLevel Level, string Text) Describe(Exception e) => e switch
    {
        Models.TaskConflictException c => (NoticeLevel.Warning, c.ChangedBy == Models.Fmt.HumanServerName
            ? "This task was changed in another tab. Showing the latest version."
            : $"{Models.Fmt.Person(c.ChangedBy)} just changed this. Showing the latest version."),
        Models.ApiException { IsOffline: true } => (NoticeLevel.Error, "You look offline, so that change was not saved. Try again when the connection is back."),
        Models.ApiException { Status: HttpStatusCode.Unauthorized } => (NoticeLevel.Info, ""),
        Models.ApiException { Status: HttpStatusCode.NotFound } => (NoticeLevel.Warning, "That no longer exists. It may have been deleted elsewhere."),
        Models.ApiException { Status: HttpStatusCode.Forbidden } a => (NoticeLevel.Error, a.Message),
        Models.ApiException { Status: HttpStatusCode.Conflict } a => (NoticeLevel.Warning, a.Message),
        Models.ApiException { Status: HttpStatusCode.BadRequest } a => (NoticeLevel.Error, a.Message),
        Models.ApiException { Status: { } s } when (int)s >= 500 => (NoticeLevel.Error, $"The server had a problem ({(int)s}), so that change was not saved."),
        Models.ApiException a => (NoticeLevel.Error, a.Message),
        _ => (NoticeLevel.Error, "Something went wrong, so that change was not saved."),
    };
}
