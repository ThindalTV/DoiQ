using System.Security.Claims;
using DoiQ.Core.Services;
using DoiQ.Server.Auth;
using DoiQ.Shared.Dtos;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace DoiQ.Server.Endpoints;

/// <summary>First-run setup, login and logout. All anonymous: they are how a caller becomes authenticated.</summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");
        auth.MapPost("/login", Login).AllowAnonymous().RequireRateLimiting(LoginLimitOptions.PolicyName);
        auth.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).AllowAnonymous();

        app.MapGet("/api/setup/status", async (AccountService a, CancellationToken ct) => Results.Ok(await a.GetStatus(ct))).AllowAnonymous();
        app.MapPost("/api/setup", Setup).AllowAnonymous().RequireRateLimiting(LoginLimitOptions.PolicyName);
        return app;
    }

    private static async Task<IResult> Login(LoginRequest req, HttpContext ctx, ActorService actors, AccountService account, PasswordVerifyGate gate, CancellationToken ct)
    {
        var ok = await gate.Run(() => account.Verify(req.Password, ct), ct);
        if (ok is null) return Results.Problem("No password is set yet. Complete first-run setup (POST /api/setup).", statusCode: 503);
        if (ok == false)
        {
            await Task.Delay(500, ct);   // on top of the per-IP rate limit
            return Results.Problem("Wrong password.", statusCode: 401);
        }
        var user = await SignInUser(ctx, actors, account, ct);
        return Results.Ok(new ActorDto(user.Id, user.Kind, user.Name, user.Initials, user.IsTriage));
    }

    private static async Task<IResult> Setup(SetupRequest req, HttpContext ctx, ActorService actors, AccountService account, PasswordVerifyGate gate, CancellationToken ct)
    {
        await gate.Run(async () => { await account.Setup(req.Password, ct); return true; }, ct);
        var user = await SignInUser(ctx, actors, account, ct);
        return Results.Ok(new ActorDto(user.Id, user.Kind, user.Name, user.Initials, user.IsTriage));
    }

    /// <summary>Issues the user's login cookie, stamped with the current password (see <see cref="AccountService.SessionStamp"/>).</summary>
    internal static async Task<ActorInfo> SignInUser(HttpContext ctx, ActorService actors, AccountService account, CancellationToken ct)
    {
        var user = await actors.GetHumanAsync(ct) ?? throw new NotFoundException("No human actor seeded.");
        var principal = ActorClaims.ToPrincipal(user, CookieAuthenticationDefaults.AuthenticationScheme);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(ActorClaims.SessionStamp, await account.SessionStamp(ct) ?? ""));
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties { IsPersistent = true });
        return user;
    }

    /// <summary>Rejects a login cookie issued before the last password change, and signs it out.</summary>
    internal static async Task ValidateSession(CookieValidatePrincipalContext ctx)
    {
        var account = ctx.HttpContext.RequestServices.GetRequiredService<AccountService>();
        var current = await account.SessionStamp(ctx.HttpContext.RequestAborted);
        if (current is not null && ctx.Principal?.FindFirstValue(ActorClaims.SessionStamp) == current) return;
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
