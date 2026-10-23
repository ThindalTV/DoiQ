using DoiQ.Core.Data;
using DoiQ.Core.Options;
using DoiQ.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DoiQ.Core;

public static class DependencyInjection
{
    /// <summary>Default SQLite connection string; Compose overrides with ConnectionStrings__Db=Data Source=/data/doiq.db.</summary>
    public static string ResolveConnectionString(IConfiguration config)
    {
        var cs = config.GetConnectionString("Db");
        if (!string.IsNullOrWhiteSpace(cs)) return cs;
        return $"Data Source={Path.Combine(AppContext.BaseDirectory, "data", "doiq.db")}";
    }

    public static IServiceCollection AddDoiQCore(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<BoardOptions>(config.GetSection(BoardOptions.Section));
        services.Configure<AuthOptions>(config.GetSection(AuthOptions.Section));
        services.TryAddSingleton(TimeProvider.System);

        var connectionString = ResolveConnectionString(config);
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
        if (!string.IsNullOrEmpty(builder.DataSource) && builder.DataSource != ":memory:")
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        builder.DefaultTimeout = Math.Max(builder.DefaultTimeout, 30);   // wait on writer locks instead of failing
        services.AddDbContext<DoiQDbContext>(o => o.UseSqlite(builder.ToString()));

        services.AddScoped<ActorService>();
        services.AddScoped<AccountService>();
        services.AddScoped<AgentAdminService>();
        services.AddScoped<TaskService>();
        services.AddScoped<BoardService>();
        services.TryAddSingleton<IBoardBroadcaster, NoopBroadcaster>();
        services.TryAddSingleton<IOutboxSignal, NoopOutboxSignal>();
        return services;
    }

    /// <summary>Migrate, enable WAL and seed the user's actor.</summary>
    public static async Task InitializeDoiQ(this IServiceProvider root, IConfiguration config, CancellationToken ct)
    {
        using var scope = root.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DoiQDbContext>();
        await db.Database.MigrateAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);

        var auth = new AuthOptions();
        config.GetSection(AuthOptions.Section).Bind(auth);
        // Only the user's actor is seeded; agents are created in Settings.
        await scope.ServiceProvider.GetRequiredService<ActorService>().Seed(auth, [], ct);
    }
}

/// <summary>Used by `dotnet ef` at design time.</summary>
public sealed class DesignTimeDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<DoiQDbContext>
{
    public DoiQDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<DoiQDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
