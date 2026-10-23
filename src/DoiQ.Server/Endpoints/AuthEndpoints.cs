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
        return await SignInUser(ctx, actors, ct);
    }

    private static async Task<IResult> Setup(SetupRequest req, HttpContext ctx, ActorService actors, AccountService account, PasswordVerifyGate gate, CancellationToken ct)
    {
        await gate.Run(async () => { await account.Setup(req.Password, ct); return true; }, ct);
        return await SignInUser(ctx, actors, ct);
    }

    private static async Task<IResult> SignInUser(HttpContext ctx, ActorService actors, CancellationToken ct)
    {
        var user = await actors.GetHumanAsync(ct) ?? throw new NotFoundException("No human actor seeded.");
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            ActorClaims.ToPrincipal(user, CookieAuthenticationDefaults.AuthenticationScheme),
            new AuthenticationProperties { IsPersistent = true });
        return Results.Ok(new ActorDto(user.Id, user.Kind, user.Name, user.Initials, user.IsTriage));
    }
}
