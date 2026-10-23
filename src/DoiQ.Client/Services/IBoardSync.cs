using DoiQ.Shared.Dtos;

namespace DoiQ.Client.Services;

/// <summary>What <see cref="BoardHubClient"/> needs from the board's data layer: apply pushed events, catch up after a gap.</summary>
public interface IBoardSync
{
    /// <summary>Loads the board if that has not happened yet (and records where event catch-up starts).</summary>
    Task EnsureLoaded(CancellationToken ct);

    /// <summary>Each Apply ignores an event whose id was already applied, so a change's own echo is harmless.</summary>
    Task ApplyAsync(TaskChangedMessage message);
    Task ApplyAsync(TaskDeletedMessage message);
    Task ApplyAsync(CommentAddedMessage message);
    Task ApplyAsync(FieldChangedMessage message);
    /// <summary>A read receipt for a task (another actor's seen marker moved). Has no event id; applied as it arrives.</summary>
    Task ApplyAsync(SeenChangedMessage message);

    /// <summary>A board was added, renamed or deleted (any board, not just the one shown).</summary>
    Task ApplyAsync(BoardChangedMessage message);

    /// <summary>Fetches /api/events after the last applied id and refreshes every task they touched.</summary>
    Task CatchUpAsync();

    /// <summary>Forgets the loaded board (sign-out); the next load starts clean.</summary>
    void Reset();
}
