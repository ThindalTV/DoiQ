namespace DoiQ.Core.Backup;

/// <summary>
/// Nightly database backup, bound from the "Backup" configuration section (e.g. Backup__Hour). Every setting has a
/// default, so nothing needs configuring.
/// </summary>
public sealed class BackupOptions
{
    public const string Section = "Backup";

    /// <summary>Set to false to switch the nightly backup off (e.g. when the host backs the folder up some other way).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Hour of the day (0-23, the container's local time, UTC unless TZ is set) when the backup runs.</summary>
    public int Hour { get; set; } = 3;

    /// <summary>How many backups to keep; older ones are deleted after each successful backup.</summary>
    public int Keep { get; set; } = 14;

    /// <summary>Where backups go. Default: a "backups" folder next to the database.</summary>
    public string? Directory { get; set; }

    /// <summary>The SQLite file to back up. Filled in from the connection string.</summary>
    public string? DatabasePath { get; set; }

    /// <summary>The Data Protection keys folder copied along with each backup. Filled in by the server.</summary>
    public string? KeysPath { get; set; }
}
