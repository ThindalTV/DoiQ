using DoiQ.Core.Data;
using DoiQ.Core.Domain;
using DoiQ.Shared.Dtos;
using DoiQ.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DoiQ.Core.Services;

/// <summary>
/// Boards: list (anyone signed in), add, rename and delete (the user only, like agent management). Boards are fully
/// separate; deleting one is refused while it holds a task that is not Done and always needs an explicit confirmation.
/// </summary>
public sealed class BoardService(DoiQDbContext db, ICurrentActor current, IBoardBroadcaster broadcaster, TimeProvider time, ILogger<BoardService> logger)
{
    public const int MaxNameLength = 60;

    private ActorInfo Me => current.Require();

    private ActorInfo RequireHuman() =>
        Me is { IsHuman: true } me ? me : throw new ForbiddenException("Only the user can add, rename or delete boards.");

    public async Task<IReadOnlyList<BoardDto>> ListAsync(CancellationToken ct = default)
    {
        _ = Me;
        var boards = await db.Boards.AsNoTracking().OrderBy(b => b.Id).ToListAsync(ct);
        var counts = await db.Tasks.IgnoreQueryFilters().AsNoTracking().GroupBy(t => t.BoardId)
            .Select(g => new { g.Key, Open = g.Count(t => t.Status != BoardStatus.Done), Done = g.Count(t => t.Status == BoardStatus.Done) })
            .ToDictionaryAsync(x => x.Key, x => (x.Open, x.Done), ct);
        return boards.Select(b => ToDto(b, counts.GetValueOrDefault(b.Id))).ToList();
    }

    public async Task<BoardDto> CreateAsync(CreateBoardRequest req, CancellationToken ct = default)
    {
        RequireHuman();
        var name = NormalizeName(req.Name);
        await EnsureNameFreeAsync(name, null, ct);
        var board = new Board { Name = name, CreatedAt = time.GetUtcNow().UtcDateTime, Version = 1 };
        db.Boards.Add(board);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw new ConflictException($"A board named '{name}' already exists."); }
        await PublishAsync(BoardChangeKind.Created, board);
        return ToDto(board, (0, 0));
    }

    public async Task<BoardDto> RenameAsync(int id, RenameBoardRequest req, CancellationToken ct = default)
    {
        RequireHuman();
        var name = NormalizeName(req.Name);
        var board = await db.Boards.FirstOrDefaultAsync(b => b.Id == id, ct) ?? throw new NotFoundException($"Board {id} not found.");
        if (req.ExpectedVersion is { } v && v != board.Version)
            throw new ConflictException($"Board '{board.Name}' was changed elsewhere (version {board.Version}, you had {v}). Reload and retry.");
        await EnsureNameFreeAsync(name, id, ct);
        if (name != board.Name)
        {
            board.Name = name;
            board.Version++;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); throw new ConflictException("The board was changed elsewhere. Reload and retry."); }
            catch (DbUpdateException) { throw new ConflictException($"A board named '{name}' already exists."); }
            await PublishAsync(BoardChangeKind.Renamed, board);
        }
        return (await ListAsync(ct)).First(b => b.Id == id);
    }

    /// <summary>
    /// Deletes a board with everything on it. <paramref name="confirm"/> must be true (an accidental bare DELETE does nothing).
    /// Refused with <see cref="BoardNotEmptyException"/> while any task is not in Done, and for the last remaining board.
    /// </summary>
    public async Task DeleteAsync(int id, bool confirm, CancellationToken ct = default)
    {
        RequireHuman();
        if (!confirm) throw new ValidationException("Deleting a board needs explicit confirmation: pass confirm=true.");
        var board = await db.Boards.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct) ?? throw new NotFoundException($"Board {id} not found.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);   // the checks and the delete see the same data
        if (await db.Boards.CountAsync(ct) <= 1) throw new ConflictException("The last remaining board cannot be deleted.");
        var tasks = db.Tasks.IgnoreQueryFilters().Where(t => t.BoardId == id);
        var open = await tasks.CountAsync(t => t.Status != BoardStatus.Done, ct);
        if (open > 0) throw new BoardNotEmptyException(board.Name, open, await tasks.CountAsync(ct));

        // Idempotency records point at rows that are about to vanish; a retry must not "find" them.
        await TaskService.RequestKeysFor(db, tasks.Select(t => t.Id)).ExecuteDeleteAsync(ct);
        await db.Outbox.Where(o => o.BoardId == id).ExecuteDeleteAsync(ct);          // pending and delivered; they reference the events
        await db.Events.Where(e => e.BoardId == id).ExecuteDeleteAsync(ct);
        await tasks.ExecuteDeleteAsync(ct);                                           // fields, comments, subtasks and read markers cascade
        await db.Boards.Where(b => b.Id == id).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);

        await PublishAsync(BoardChangeKind.Deleted, board);
    }

    // ───────────────────────── helpers ─────────────────────────

    /// <summary>Trimmed, 1-60 characters.</summary>
    public static string NormalizeName(string? name)
    {
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n)) throw new ValidationException("A board name is required.");
        if (n.Length > MaxNameLength) throw new ValidationException($"A board name can be at most {MaxNameLength} characters.");
        return n;
    }

    private async Task EnsureNameFreeAsync(string name, int? exceptId, CancellationToken ct)
    {
        // In memory: SQLite's NOCASE only folds ASCII, and there are few boards.
        var names = await db.Boards.AsNoTracking().Select(b => new { b.Id, b.Name }).ToListAsync(ct);
        if (names.Any(b => b.Id != exceptId && b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new ConflictException($"A board named '{name}' already exists.");
    }

    private async Task PublishAsync(BoardChangeKind kind, Board board)
    {
        try { await broadcaster.BoardChangedAsync(new BoardChangedMessage(kind, board.Id, board.Name)); }
        catch (Exception ex) { logger.LogWarning(ex, "Board broadcast failed"); }
    }

    private static BoardDto ToDto(Board b, (int Open, int Done) counts) =>
        new(b.Id, b.Name, counts.Open, counts.Done, b.CreatedAt, b.Version);
}

/// <summary>Finds the board a call is about: by id, by name, or (neither given) the only board.</summary>
public static class BoardResolver
{
    public static Board Pick(IReadOnlyList<Board> boards, int? id, string? name)
    {
        if (id is { } bid)
            return boards.FirstOrDefault(b => b.Id == bid) ?? throw new NotFoundException($"Board {bid} not found. Boards: {List(boards)}.");
        if (!string.IsNullOrWhiteSpace(name))
        {
            var n = name.Trim();
            return boards.FirstOrDefault(b => b.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                ?? throw new NotFoundException($"No board named '{n}'. Boards: {List(boards)}.");
        }
        if (boards.Count == 1) return boards[0];
        throw new ValidationException($"There are {boards.Count} boards, so say which one with boardId or boardName. Boards: {List(boards)}.");
    }

    private static string List(IEnumerable<Board> boards) => string.Join(", ", boards.Select(b => $"{b.Name} (id {b.Id})"));
}
