using DoiQ.Core.Backup;
using DoiQ.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DoiQ.Tests;

public sealed class BackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "doiq-backup-tests", Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromMinutes(1));
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 24, 3, 0, 0, TimeSpan.Zero));

    private CancellationToken Ct => _cts.Token;
    private string DbPath => Path.Combine(_root, "doiq.db");
    private string BackupDir => Path.Combine(_root, "backups");
    private string KeysDir => Path.Combine(_root, "keys");

    public BackupServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _cts.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private BackupService Service(int keep = 14) =>
        new(Options.Create(new BackupOptions { DatabasePath = DbPath, KeysPath = KeysDir, Keep = keep }), _clock, NullLogger<BackupService>.Instance);

    private async Task CreateDatabaseWithOneSetting(CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<DoiQDbContext>().UseSqlite($"Data Source={DbPath};Pooling=False").Options;
        await using var db = new DoiQDbContext(options, _clock);
        await db.Database.MigrateAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        db.Settings.Add(new DoiQ.Core.Domain.AppSetting { Key = "marker", Value = "kept" });
        await db.SaveChangesAsync(ct);
    }

    private static async Task<string?> ReadMarker(string file, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Settings WHERE Key = 'marker'";
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    [Fact]
    public async Task BackupService_RunWithADatabase_WritesACopyThatHoldsItsData()
    {
        // Arrange
        await CreateDatabaseWithOneSetting(Ct);

        // Act
        var file = await Service().Run(Ct);

        // Assert
        Assert.Equal(Path.Combine(BackupDir, "doiq-20261024T030000Z.db"), file);
        Assert.Equal("kept", await ReadMarker(file!, Ct));
    }

    [Fact]
    public async Task BackupService_RunWithoutADatabaseFile_WritesNothing()
    {
        // Arrange
        var service = Service();

        // Act
        var file = await service.Run(Ct);

        // Assert
        Assert.Null(file);
        Assert.False(Directory.Exists(BackupDir));
    }

    [Fact]
    public async Task BackupService_RunWithAKeysFolder_CopiesTheKeys()
    {
        // Arrange
        await CreateDatabaseWithOneSetting(Ct);
        Directory.CreateDirectory(KeysDir);
        await File.WriteAllTextAsync(Path.Combine(KeysDir, "key-1.xml"), "<key/>", Ct);

        // Act
        await Service().Run(Ct);

        // Assert
        Assert.Equal("<key/>", await File.ReadAllTextAsync(Path.Combine(BackupDir, "keys", "key-1.xml"), Ct));
    }

    [Fact]
    public async Task BackupService_MoreBackupsThanKeep_DeletesTheOldestOnes()
    {
        // Arrange
        await CreateDatabaseWithOneSetting(Ct);
        var service = Service(keep: 2);
        await service.Run(Ct);
        _clock.Advance(TimeSpan.FromDays(1));
        await service.Run(Ct);
        _clock.Advance(TimeSpan.FromDays(1));

        // Act
        await service.Run(Ct);

        // Assert
        var names = Directory.GetFiles(BackupDir, "doiq-*.db").Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["doiq-20261025T030000Z.db", "doiq-20261026T030000Z.db"], names);
    }

    [Fact]
    public async Task BackupService_SuccessfulRun_LeavesNoPartialFile()
    {
        // Arrange
        await CreateDatabaseWithOneSetting(Ct);

        // Act
        await Service().Run(Ct);

        // Assert
        Assert.Empty(Directory.GetFiles(BackupDir, "*.partial"));
    }

    [Theory]
    [InlineData("2026-10-24T01:30:00+00:00", 3, "2026-10-24T03:00:00+00:00")]
    [InlineData("2026-10-24T03:00:00+00:00", 3, "2026-10-25T03:00:00+00:00")]
    [InlineData("2026-10-24T22:15:00+02:00", 3, "2026-10-25T03:00:00+02:00")]
    public void BackupSchedule_NextRun_IsTheNextOccurrenceOfTheHour(string now, int hour, string expected)
    {
        // Arrange
        var at = DateTimeOffset.Parse(now);

        // Act
        var next = BackupService.NextRun(at, hour);

        // Assert
        Assert.Equal(DateTimeOffset.Parse(expected), next);
    }
}
