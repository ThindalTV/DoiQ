using DoiQ.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DoiQ.Tests;

/// <summary>The Boards migration run over a database created with the schema that shipped before boards existed.</summary>
public sealed class BoardMigrationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"doiq-migrate-{Guid.NewGuid():N}.db");

    private DbContextOptions<DoiQDbContext> Options => new DbContextOptionsBuilder<DoiQDbContext>().UseSqlite($"Data Source={_path};Pooling=False").Options;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*")) { try { File.Delete(f); } catch { } }
    }

    /// <summary>Builds the previous schema and fills it with a user, an agent and two tasks with events, an outbox row and a comment.</summary>
    private async Task CreateOldDatabaseAsync()
    {
        await using var db = new DoiQDbContext(Options);
        await db.GetService<IMigrator>().MigrateAsync("AgentManagement");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO Actors (Id, Kind, Name, Initials, IsTriage, IsActive, CreatedAt) VALUES
              (1, 'Human', 'User', 'U', 0, 1, '2026-10-01 00:00:00'),
              (2, 'Agent', 'Hermes', 'H', 1, 1, '2026-10-01 00:00:00');
            INSERT INTO Tasks (Id, Title, Description, Status, AssignedToPool, CreatedById, CreatedAt, UpdatedAt, SortOrder, Source, SourceRef, LastActivityAt, LastActivityById, Version) VALUES
              (1, 'Old open task', '', 'Doing', 0, 1, '2026-10-01 00:00:00', '2026-10-01 00:00:00', 0, 'Email', 'msg-1', '2026-10-01 00:00:00', 1, 3),
              (2, 'Old done task', '', 'Done',  0, 1, '2026-10-01 00:00:00', '2026-10-01 00:00:00', 0, 'Manual', NULL, '2026-10-01 00:00:00', 1, 1);
            INSERT INTO Events (Id, TaskId, ActorId, Type, PayloadJson, At) VALUES
              (1, 1, 1, 'Created', NULL, '2026-10-01 00:00:00'),
              (2, 2, 1, 'Created', NULL, '2026-10-01 00:00:00');
            INSERT INTO Outbox (Id, Kind, TaskId, TargetActorId, EventId, CreatedAt, NotBefore, Attempts) VALUES
              (1, 'Todo', 1, 2, 1, '2026-10-01 00:00:00', '2026-10-01 00:00:00', 0);
            INSERT INTO Comments (Id, TaskId, AuthorId, Body, CreatedAt) VALUES (1, 1, 1, 'Remember this', '2026-10-01 00:00:00');
            """);
    }

    [Fact]
    public async Task Migrate_DatabaseFromBeforeBoards_CreatesMainAndAssignsEveryExistingRowToIt()
    {
        // Arrange
        await CreateOldDatabaseAsync();

        // Act
        await using (var db = new DoiQDbContext(Options)) await db.Database.MigrateAsync();

        // Assert
        await using var after = new DoiQDbContext(Options);
        var main = await after.Boards.SingleAsync();
        Assert.Equal("Main", main.Name);
        var tasks = await after.Tasks.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(2, tasks.Count);
        Assert.All(tasks, t => Assert.Equal(main.Id, t.BoardId));
        Assert.All(await after.Events.ToListAsync(), e => Assert.Equal(main.Id, e.BoardId));
        Assert.Equal(main.Id, (await after.Outbox.SingleAsync()).BoardId);
    }

    [Fact]
    public async Task Migrate_DatabaseFromBeforeBoards_KeepsTheExistingDataIntact()
    {
        // Arrange
        await CreateOldDatabaseAsync();

        // Act
        await using (var db = new DoiQDbContext(Options)) await db.Database.MigrateAsync();

        // Assert
        await using var after = new DoiQDbContext(Options);
        var open = await after.Tasks.IgnoreQueryFilters().SingleAsync(t => t.Id == 1);
        Assert.Equal(("Old open task", "msg-1", 3L), (open.Title, open.SourceRef, open.Version));
        Assert.Equal("Remember this", (await after.Comments.SingleAsync()).Body);
        Assert.Equal(2, await after.Events.CountAsync());
    }

    [Fact]
    public async Task Migrate_DatabaseFromBeforeBoards_LeavesNoForeignKeyViolations()
    {
        // Arrange
        await CreateOldDatabaseAsync();

        // Act
        await using (var db = new DoiQDbContext(Options)) await db.Database.MigrateAsync();

        // Assert
        await using var after = new DoiQDbContext(Options);
        var violations = await after.Database.SqlQueryRaw<string>("SELECT \"table\" AS Value FROM pragma_foreign_key_check").ToListAsync();
        Assert.Empty(violations);
    }

    [Fact]
    public async Task Migrate_DatabaseFromBeforeBoards_KeepsSourceRefUniqueWithinTheBoard()
    {
        // Arrange
        await CreateOldDatabaseAsync();
        await using (var db = new DoiQDbContext(Options)) await db.Database.MigrateAsync();

        // Act
        await using var after = new DoiQDbContext(Options);
        var main = await after.Boards.SingleAsync();
        after.Tasks.Add(new DoiQ.Core.Domain.TaskItem { BoardId = main.Id, Title = "Duplicate", Status = DoiQ.Shared.Enums.BoardStatus.New, CreatedById = 1, LastActivityById = 1, Version = 1, SourceRef = "msg-1" });
        var ex = await Record.ExceptionAsync(() => after.SaveChangesAsync());

        // Assert
        Assert.IsType<DbUpdateException>(ex);
    }

    [Fact]
    public async Task Migrate_FreshDatabase_StartsWithOneBoardNamedMain()
    {
        // Arrange
        // (no database file yet)

        // Act
        await using (var db = new DoiQDbContext(Options)) await db.Database.MigrateAsync();

        // Assert
        await using var after = new DoiQDbContext(Options);
        Assert.Equal(["Main"], await after.Boards.Select(b => b.Name).ToListAsync());
    }
}
