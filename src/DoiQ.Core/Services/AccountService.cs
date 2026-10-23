using DoiQ.Core.Data;
using DoiQ.Core.Domain;
using DoiQ.Shared.Dtos;
using Microsoft.EntityFrameworkCore;

namespace DoiQ.Core.Services;

/// <summary>The user's password, stored as a hash in the database: first-run setup, verification and change.</summary>
public sealed class AccountService(DoiQDbContext db)
{
    public const string PasswordKey = "password-hash";
    public const int MinPasswordLength = 8;

    private Task<AppSetting?> Stored(CancellationToken ct) => db.Settings.FirstOrDefaultAsync(s => s.Key == PasswordKey, ct);

    public async Task<SetupStatusDto> GetStatus(CancellationToken ct) => new(await Stored(ct) is null);

    /// <summary>Stores the first password. 409 once a password exists.</summary>
    public async Task Setup(string password, CancellationToken ct)
    {
        if (!(await GetStatus(ct)).NeedsSetup) throw new ConflictException("Setup has already been completed.");
        RequireValid(password);
        db.Settings.Add(new AppSetting { Key = PasswordKey, Value = PasswordHasher.Hash(password) });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw new ConflictException("Setup has already been completed."); }   // lost the race
    }

    /// <summary>True/false once a password is set; null before first-run setup.</summary>
    public async Task<bool?> Verify(string? password, CancellationToken ct) =>
        await Stored(ct) is { } stored ? PasswordHasher.Verify(password ?? "", stored.Value) : null;

    public async Task Change(string current, string @new, CancellationToken ct)
    {
        var stored = await Stored(ct) ?? throw new ConflictException("No password is set yet.");
        if (!PasswordHasher.Verify(current ?? "", stored.Value)) throw new ValidationException("The current password is wrong.");
        RequireValid(@new);
        stored.Value = PasswordHasher.Hash(@new);
        await db.SaveChangesAsync(ct);
    }

    private static void RequireValid(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            throw new ValidationException($"The password must be at least {MinPasswordLength} characters.");
    }
}
