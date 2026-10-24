using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DoiQ.Core.Backup;

/// <summary>
/// Writes a consistent copy of the SQLite database with <c>VACUUM INTO</c> (safe while the app runs in WAL mode;
/// never copy the file by hand), checks its integrity, copies the Data Protection keys next to it and keeps the
/// newest <see cref="BackupOptions.Keep"/> copies.
/// </summary>
public sealed class BackupService(IOptions<BackupOptions> options, TimeProvider time, ILogger<BackupService> logger)
{
    private const string Prefix = "doiq-";
    private const string Extension = ".db";

    /// <summary>Runs one backup and returns the new file's path, or null when there is no database file to back up.</summary>
    public async Task<string?> Run(CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrEmpty(o.DatabasePath) || o.DatabasePath == ":memory:" || !File.Exists(o.DatabasePath))
        {
            logger.LogInformation("Backup skipped: no database file at {Path}", o.DatabasePath);
            return null;
        }

        var dir = o.Directory ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(o.DatabasePath))!, "backups");
        Directory.CreateDirectory(dir);
        var stamp = time.GetUtcNow().ToString("yyyyMMdd'T'HHmmss'Z'");
        var partial = Path.Combine(dir, $".{Prefix}{stamp}{Extension}.partial");
        var target = Path.Combine(dir, $"{Prefix}{stamp}{Extension}");

        File.Delete(partial);
        await Execute(o.DatabasePath, SqliteOpenMode.ReadWrite, "VACUUM INTO $path", ("$path", partial), ct);
        var check = await Execute(partial, SqliteOpenMode.ReadOnly, "PRAGMA integrity_check", null, ct);
        if (!string.Equals(check as string, "ok", StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            throw new InvalidOperationException($"Backup failed the integrity check: {check}");
        }
        File.Move(partial, target, overwrite: true);

        CopyKeys(o.KeysPath, Path.Combine(dir, "keys"));
        Prune(dir, Math.Max(1, o.Keep));
        logger.LogInformation("Backup written to {Path}", target);
        return target;
    }

    /// <summary>The next time the backup should run: today at <paramref name="hour"/> if that is still ahead, else tomorrow.</summary>
    public static DateTimeOffset NextRun(DateTimeOffset now, int hour)
    {
        var today = new DateTimeOffset(now.Year, now.Month, now.Day, Math.Clamp(hour, 0, 23), 0, 0, now.Offset);
        return today > now ? today : today.AddDays(1);
    }

    private static async Task<object?> Execute(string file, SqliteOpenMode mode, string sql, (string Name, string Value)? parameter, CancellationToken ct)
    {
        // No pooling, so the file handles are released as soon as the backup is done.
        var cs = new SqliteConnectionStringBuilder { DataSource = file, Mode = mode, Pooling = false, DefaultTimeout = 30 }.ToString();
        await using var conn = new SqliteConnection(cs);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (parameter is { } p) cmd.Parameters.AddWithValue(p.Name, p.Value);
        return await cmd.ExecuteScalarAsync(ct);
    }

    private static void CopyKeys(string? source, string target)
    {
        if (string.IsNullOrEmpty(source) || !Directory.Exists(source)) return;
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
    }

    private void Prune(string dir, int keep)
    {
        // Names sort chronologically, so everything before the newest `keep` goes.
        var old = Directory.GetFiles(dir, $"{Prefix}*{Extension}").Order(StringComparer.Ordinal).SkipLast(keep);
        foreach (var file in old)
        {
            File.Delete(file);
            logger.LogInformation("Backup removed {Path}", file);
        }
    }
}
