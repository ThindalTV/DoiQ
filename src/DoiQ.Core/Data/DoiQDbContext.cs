using DoiQ.Core.Domain;
using DoiQ.Core.Options;
using DoiQ.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DoiQ.Core.Data;

public class DoiQDbContext : DbContext
{
    private readonly TimeProvider _time;
    private readonly int _archiveAfterDays;

    public DoiQDbContext(DbContextOptions<DoiQDbContext> options, TimeProvider? time = null, Microsoft.Extensions.Options.IOptions<BoardOptions>? board = null)
        : base(options)
    {
        _time = time ?? TimeProvider.System;
        _archiveAfterDays = board?.Value.ArchiveAfterDays ?? new BoardOptions().ArchiveAfterDays;
    }

    public DbSet<Board> Boards => Set<Board>();
    public DbSet<Actor> Actors => Set<Actor>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<Subtask> Subtasks => Set<Subtask>();
    public DbSet<InfoField> Fields => Set<InfoField>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<ActivityEvent> Events => Set<ActivityEvent>();
    public DbSet<ReadMarker> ReadMarkers => Set<ReadMarker>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<RequestKey> RequestKeys => Set<RequestKey>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();

    /// <summary>Evaluated per query by the archive filter on <see cref="Tasks"/>.</summary>
    public DateTime ArchiveCutoff => _time.GetUtcNow().UtcDateTime.AddDays(-_archiveAfterDays);

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        b.Properties<BoardStatus>().HaveConversion<string>().HaveMaxLength(16);
        b.Properties<ActorKind>().HaveConversion<string>().HaveMaxLength(16);
        b.Properties<EventType>().HaveConversion<string>().HaveMaxLength(32);
        b.Properties<TaskSource>().HaveConversion<string>().HaveMaxLength(16);
        b.Properties<OutboxKind>().HaveConversion<string>().HaveMaxLength(16);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Actor>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.ApiKeyHash).IsUnique();
            e.Property(x => x.Name).HasMaxLength(64);
            e.Property(x => x.Initials).HasMaxLength(8);
        });

        b.Entity<Board>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(60).UseCollation("NOCASE");
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Version).IsConcurrencyToken();
        });

        b.Entity<TaskItem>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<Board>().WithMany().HasForeignKey(x => x.BoardId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.BoardId, x.SourceRef }).IsUnique().HasFilter("SourceRef IS NOT NULL");
            e.HasIndex(x => new { x.BoardId, x.Status, x.SortOrder });
            // The archive: count, keyset paging newest-done first (DoneAt, then Id, which SQLite keeps in every index).
            e.HasIndex(x => new { x.BoardId, x.Status, x.DoneAt });
            e.HasIndex(x => x.AssigneeId);
            e.HasOne<Actor>().WithMany().HasForeignKey(x => x.AssigneeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Actor>().WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Actor>().WithMany().HasForeignKey(x => x.LastActivityById).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Actor>().WithMany().HasForeignKey(x => x.ClaimedById).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Fields).WithOne().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Comments).WithOne().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Subtasks).WithOne().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
            // Archive: Done tasks older than the configured window are hidden unless IgnoreQueryFilters().
            e.HasQueryFilter(t => t.Status != BoardStatus.Done || t.DoneAt == null || t.DoneAt >= ArchiveCutoff);
        });

        b.Entity<Subtask>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(500);
            e.HasOne<Actor>().WithMany().HasForeignKey(x => x.AssigneeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Actor>().WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<InfoField>(e =>
        {
            e.Property(x => x.Label).HasMaxLength(100).UseCollation("NOCASE");
            e.HasIndex(x => new { x.TaskId, x.Label }).IsUnique();
            e.HasOne<Actor>().WithMany().HasForeignKey(x => x.AuthorId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Comment>(e =>
        {
            e.HasOne<Actor>().WithMany().HasForeignKey(x => x.AuthorId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ActivityEvent>(e =>
        {
            e.HasIndex(x => x.TaskId);
            e.HasIndex(x => x.BoardId);
            e.HasOne<Board>().WithMany().HasForeignKey(x => x.BoardId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Actor>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ReadMarker>(e =>
        {
            e.HasKey(x => new { x.TaskId, x.ActorId });
            e.HasOne<TaskItem>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Actor>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.HasIndex(x => new { x.SentAt, x.NotBefore });
            e.HasIndex(x => new { x.Kind, x.TaskId, x.TargetActorId });
            e.HasIndex(x => x.BoardId);
            e.HasOne<Board>().WithMany().HasForeignKey(x => x.BoardId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Event).WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Actor>().WithMany().HasForeignKey(x => x.TargetActorId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AppSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
        });

        b.Entity<RequestKey>(e =>
        {
            e.HasIndex(x => new { x.ActorId, x.Key }).IsUnique();
            e.Property(x => x.Key).HasMaxLength(200);
        });
    }

    private sealed class UtcDateTimeConverter()
        : ValueConverter<DateTime, DateTime>(v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}
