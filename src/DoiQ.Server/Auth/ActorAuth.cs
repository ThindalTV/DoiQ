using System.Security.Claims;
using System.Text.Encodings.Web;
using DoiQ.Core.Services;
using DoiQ.Shared.Enums;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DoiQ.Server.Auth;

public static class Policies
{
    public const string UserOnly = "UserOnly";
}

public static class ActorClaims
{
    public const string ApiKeyScheme = "ApiKey";
    public const string ApiKeyHeader = "X-Api-Key";

    /// <summary>The login cookie's password stamp; a cookie whose stamp is out of date is rejected.</summary>
    public const string SessionStamp = "session_stamp";

    public static ClaimsPrincipal ToPrincipal(ActorInfo a, string scheme)
    {
        var id = new ClaimsIdentity(
        [
            new Claim("actor_id", a.Id.ToString()),
            new Claim("actor_kind", a.Kind.ToString()),
            new Claim("actor_initials", a.Initials),
            new Claim("actor_triage", a.IsTriage.ToString()),
            new Claim(ClaimTypes.Name, a.Name),
        ], scheme);
        return new ClaimsPrincipal(id);
    }

    public static ActorInfo? FromPrincipal(ClaimsPrincipal? p)
    {
        if (p?.Identity?.IsAuthenticated != true) return null;
        if (!int.TryParse(p.FindFirstValue("actor_id"), out var id)) return null;
        var kind = Enum.TryParse<ActorKind>(p.FindFirstValue("actor_kind"), out var k) ? k : ActorKind.Human;
        return new ActorInfo(id, kind, p.FindFirstValue(ClaimTypes.Name) ?? "?", p.FindFirstValue("actor_initials") ?? "?",
            bool.TryParse(p.FindFirstValue("actor_triage"), out var t) && t);
    }
}

/// <summary>Reads the actor from whichever scheme authenticated the request (cookie = the user, API key = that agent).</summary>
public sealed class HttpCurrentActor(IHttpContextAccessor accessor) : ICurrentActor
{
    public ActorInfo? Current => ActorClaims.FromPrincipal(accessor.HttpContext?.User);
}

/// <summary>X-Api-Key: &lt;key&gt; (or Authorization: Bearer &lt;key&gt;) identifies the agent the key belongs to.</summary>
public sealed class ApiKeyAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ActorService actors)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? key = Request.Headers[ActorClaims.ApiKeyHeader];
        if (string.IsNullOrEmpty(key) && Request.Headers.Authorization.ToString() is { } auth && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            key = auth["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(key)) return AuthenticateResult.NoResult();

        var actor = await actors.AuthenticateApiKeyAsync(key, Context.RequestAborted);
        if (actor is null) return AuthenticateResult.Fail("Invalid API key.");
        return AuthenticateResult.Success(new AuthenticationTicket(ActorClaims.ToPrincipal(actor, Scheme.Name), Scheme.Name));
    }
}
