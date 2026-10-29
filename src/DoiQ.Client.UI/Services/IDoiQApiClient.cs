using DoiQ.Client.Models;

namespace DoiQ.Client.Services;

/// <summary>
/// What the UI needs from the backend: <see cref="DoiQApiClient"/> against the server, <see cref="FakeDoiQApiClient"/> in memory.
/// </summary>
public interface IDoiQApiClient
{
    /// <summary>Raised when something changed elsewhere (a SignalR push was applied); the board re-reads <see cref="GetBoardAsync"/>.</summary>
    event Action? Changed;

    IReadOnlyList<string> Agents { get; }

    /// <summary>The agent that passes "any agent" tasks on to the best-placed agent, or null when there is none (the agents then race to claim them).</summary>
    string? Dispatcher { get; }

    // ---- boards: the client shows one at a time; tasks, events and live updates are those of the selected board ----

    /// <summary>The boards as last read from the server (id order).</summary>
    IReadOnlyList<BoardDto> Boards { get; }

    /// <summary>The board the loaded tasks belong to (0 until one is selected or loaded).</summary>
    int CurrentBoardId { get; }

    /// <summary>Raised when the list of boards (or a name) changed, here or in another tab.</summary>
    event Action? BoardsChanged;

    /// <summary>Reads the boards (with task counts) from the server.</summary>
    Task<IReadOnlyList<BoardDto>> GetBoardsAsync();

    /// <summary>Makes a board the current one. The next <see cref="GetBoardAsync"/> loads its tasks; a no-op when it already is.</summary>
    Task SelectBoardAsync(int boardId);

    Task<BoardDto> CreateBoardAsync(string name);
    /// <summary>A stale expectedVersion (someone renamed it first) throws an <see cref="ApiException"/> with status 409.</summary>
    Task<BoardDto> RenameBoardAsync(int id, string name, long? expectedVersion);
    /// <summary>Always sends the confirmation the server requires; the server still refuses while the board holds a task that is not Done.</summary>
    Task DeleteBoardAsync(int id);

    /// <summary>
    /// The current board's active tasks (archived ones are not on the board), in lane/priority order. Filtering is done
    /// by the board. Loads the board on first use; a task loaded this way has counts but no lists until <see cref="LoadTask"/>.
    /// </summary>
    Task<IReadOnlyList<TaskVm>> GetBoard(CancellationToken ct);

    /// <summary>How many tasks of the current board are archived (Done for longer than the archive window).</summary>
    int ArchivedCount { get; }

    /// <summary>A task this client holds: on the board, or an archived one opened from the archive view or a search.</summary>
    TaskVm? Find(int id);

    /// <summary>Loads one task with its description and lists (what the drawer shows). Null when it is gone or on another board.</summary>
    Task<TaskVm?> LoadTask(int id, CancellationToken ct);

    /// <summary>A page of a board's archive, most recently done first; <paramref name="query"/> searches it like the board search.</summary>
    Task<ArchivePageVm> GetArchivePage(int boardId, string? query, string? cursor, CancellationToken ct);

    /// <summary>Searches the current board: the matching active tasks and the first few archived ones.</summary>
    Task<BoardSearchVm> SearchBoard(string query, CancellationToken ct);

    Task<PresenceVm> GetPresenceAsync();

    Task<TaskVm> CreateTaskAsync(string title);
    /// <summary>
    /// The text edits take the task Version the edit started from (baseVersion; null = the version the client holds now).
    /// A stale one throws <see cref="TaskConflictException"/> after the task was reloaded; moves and assigns always use the held version.
    /// </summary>
    Task SetTitleAsync(int id, string title, long? baseVersion = null);
    Task SetDescriptionAsync(int id, string description, long? baseVersion = null);
    Task SetDueAsync(int id, DateOnly? due, long? baseVersion = null);

    /// <summary>Moves to a lane; beforeId places it above that task, null puts it at the bottom.</summary>
    Task MoveAsync(int id, BoardLane lane, int? beforeId);
    /// <summary>Puts the task on another board, in the same lane; it leaves the current board. The board counts are re-read.</summary>
    Task MoveToBoard(int id, int boardId, CancellationToken ct);
    Task AssignAsync(int id, AssigneeVm assignee);
    Task DeleteTaskAsync(int id);
    Task MarkSeenAsync(int id);

    Task AddSubtaskAsync(int id, string title);
    Task SetSubtaskDoneAsync(int id, int subtaskId, bool done);
    Task AddFieldAsync(int id, string name, string body);
    Task UpdateFieldAsync(int id, int fieldId, string name, string body);
    Task AddCommentAsync(int id, string body);

    // ---- session, setup and administration ----

    /// <summary>Raised when the server answers 401 to a call, so the shell can send the user to login.</summary>
    event Action? Unauthorized;

    Task<SetupStatusDto> GetSetupStatusAsync();
    /// <summary>First-run: sets the password. The server signs the user in; throws <see cref="ApiException"/> on failure.</summary>
    Task SetupAsync(string password);
    Task<bool> IsSignedInAsync();
    Task LoginAsync(string password);
    Task LogoutAsync();
    Task ChangePasswordAsync(string current, string newPassword);

    Task<IReadOnlyList<AgentAdminDto>> GetAgentsAsync();
    Task<CreatedAgentResult> CreateAgentAsync(CreateAgentRequest request);
    Task<AgentAdminDto> UpdateAgentAsync(int id, UpdateAgentRequest request);
    Task<RotatedKeyResult> RotateAgentKeyAsync(int id);
    Task SetAgentActiveAsync(int id, bool active);
    /// <summary>Refused (400) while open tasks are assigned to the agent; one with history stays in it as "Name (deleted)".</summary>
    Task<DeletedAgentResult> DeleteAgent(int id, CancellationToken ct);

    /// <summary>Gives the agent a task asking it to describe its responsibilities; it fills them in itself when it picks the task up.</summary>
    Task AskForResponsibilities(int agentId, CancellationToken ct);
}
