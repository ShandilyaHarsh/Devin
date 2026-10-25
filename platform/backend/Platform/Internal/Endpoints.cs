using Barebone.Platform.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Barebone.Platform.Internal;

internal sealed record LoginRequest(string? UserId);

internal sealed record SessionResponse(bool Authenticated, PlatformUser? User, string CsrfToken, DateTimeOffset? ExpiresAt);

internal sealed record DevUserDto(string Id, string DisplayName, IReadOnlyList<string> Roles);

internal sealed record AccessResponse(string Slug, AppStage? Stage, bool Allowed, string Reason);

internal sealed record AuditEventDto(
    long Id, string Timestamp, string AppSlug, string ActorId, string Action, string ResourceType, string? ResourceId,
    string Outcome, string? Reason, string? Before, string? After, string CorrelationId, string PrevHash, string Hash);

internal sealed record Capability(string Id, string Name, string Description, string HowToSee);

internal static class PlatformEndpoints
{
    public const string LoginRateLimitPolicy = "barebone-login";

    private static readonly Capability[] Capabilities =
    [
        new("bff-session", "BFF session", "Server-side sessions in an HttpOnly, Secure, SameSite=Strict __Host- cookie; no tokens in the browser; idle and absolute expiry.", "Log in and inspect cookies in dev tools: only __Host-session, not readable from JavaScript."),
        new("csrf", "CSRF protection", "Every mutation must send X-CSRF-Token bound to the session (or the pre-session __Host-csrf cookie).", "POST without the header returns 403 \"CSRF token missing or invalid\"."),
        new("deny-by-default", "Deny-by-default authorization", "Per-app YAML policy with allow/deny rules and conditions; any deny wins; no match denies; endpoints without RequireAction are denied.", "Call an app action with the wrong role: 403 with a reason, and a denied audit row."),
        new("staged-access", "Staged access", "Apps move builder-only -> demo -> live from the catalog; live uses an explicit access list.", "Open an app as a non-builder while it is builder-only: access denied."),
        new("audit-chain", "Tamper-evident audit", "Every write and denial is an append-only audit row chained with HMAC-SHA256.", "Audit page -> Verify audit chain; edit a row in SQLite and verify again."),
        new("unit-of-work", "Atomic unit of work", "Business change, audit row, idempotency record and outbox messages commit in one transaction.", "Trigger an action and inspect the SQLite tables: all rows or none."),
        new("idempotency", "Idempotent mutations", "Mutations require Idempotency-Key; retries replay the stored response without re-running the handler.", "Repeat a POST with the same key: response header Idempotency-Replayed: true."),
        new("outbox", "Transactional outbox", "Events are committed with the business change and relayed once by a background worker.", "Watch the host logs for 'Outbox message ... published' after an action."),
        new("pii-masking", "PII masking", "PII fields are masked in API responses and audit diffs; reveals are explicit, authorized and audited as reads.", "Audit rows show \"***\" for PII fields."),
        new("manifest-validation", "Fail-closed manifests", "platform.yaml and policy.yaml are validated at startup; any error stops the host (no card data, classification rules).", "Break a manifest (cardData: pan) and start the host: it refuses to start."),
        new("security-headers", "Security headers", "Strict CSP, HSTS, nosniff, no-referrer, frame-ancestors none, COOP and Permissions-Policy on every response.", "Inspect any response's headers in dev tools."),
        new("synthetic-data", "Synthetic data only", "Deterministic fake data (example.test emails, 9xxx last-4 values); no real PII, no card numbers.", "Open a newly approved app: it is pre-seeded with obviously fake records."),
    ];

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        app.MapGet("/api/session", GetSession);
        app.MapGet("/api/auth/dev-users", async (IIdentityProvider idp, CancellationToken ct) =>
            idp.Name != "dev"
                ? Results.NotFound()
                : Results.Ok((await idp.ListUsersAsync(ct)).Select(u => new DevUserDto(u.Id, u.DisplayName, u.Roles))));
        app.MapPost("/api/auth/login", Login).RequireRateLimiting(LoginRateLimitPolicy);
        app.MapPost("/api/auth/logout", (HttpContext ctx, ISessionStore store) =>
        {
            if (ctx.Request.Cookies.TryGetValue(PlatformMiddleware.SessionCookie, out var sid))
            {
                store.Remove(sid);
            }

            ctx.Response.Cookies.Delete(PlatformMiddleware.SessionCookie, PlatformMiddleware.HostCookie());
            return Results.NoContent();
        });

        var platform = app.MapGroup("/api/platform");
        platform.MapGet("/catalog", async (HttpContext ctx, ICatalog catalog, CancellationToken ct) =>
            await Guard(ctx, "catalog.read") ?? Results.Ok(await catalog.ListAsync(ct)));
        platform.MapGet("/catalog/{slug}", async (string slug, HttpContext ctx, ICatalog catalog, CancellationToken ct) =>
            await Guard(ctx, "catalog.read")
            ?? (await catalog.GetAsync(slug, ct) is { } entry ? Results.Ok(entry) : Problems.Result(ctx, 404, "Not found")));
        platform.MapGet("/apps/{slug}/access", AppAccess);
        platform.MapGet("/audit", ListAudit);
        platform.MapGet("/audit/verify", async (HttpContext ctx, AuditVerifier verifier, CancellationToken ct) =>
            await Guard(ctx, "audit.verify") ?? Results.Ok(await verifier.VerifyAsync(ct)));
        platform.MapGet("/capabilities", (HttpContext ctx) =>
            ctx.Items[HttpItems.User] is null ? Problems.Result(ctx, 401, "Authentication required") : Results.Ok(Capabilities));

        app.Map("/api/{**rest}", (HttpContext ctx) => Problems.Result(ctx, 404, "Not found"));
    }

    private static IResult GetSession(HttpContext ctx, ISessionStore store)
    {
        if (ctx.Items[HttpItems.Session] is SessionData session && ctx.Items[HttpItems.User] is PlatformUser user)
        {
            return Results.Ok(new SessionResponse(true, user, session.CsrfToken, store.ExpiresAt(session)));
        }

        if (!ctx.Request.Cookies.TryGetValue(PlatformMiddleware.CsrfCookie, out var csrf) || string.IsNullOrEmpty(csrf) || csrf.Length != 43)
        {
            csrf = Tokens.NewToken();
            ctx.Response.Cookies.Append(PlatformMiddleware.CsrfCookie, csrf, PlatformMiddleware.HostCookie());
        }

        return Results.Ok(new SessionResponse(false, null, csrf, null));
    }

    private static async Task<IResult> Login(HttpContext ctx, LoginRequest? body, IIdentityProvider idp, ISessionStore store, AuditWriter audit, CancellationToken ct)
    {
        var user = string.IsNullOrWhiteSpace(body?.UserId) ? null : await idp.FindUserAsync(body.UserId, ct);
        if (user is null)
        {
            return Problems.Result(ctx, StatusCodes.Status401Unauthorized, "Invalid login", "unknown-user");
        }

        if (ctx.Request.Cookies.TryGetValue(PlatformMiddleware.SessionCookie, out var old))
        {
            store.Remove(old);
        }

        var session = store.Create(user.Id);
        ctx.Response.Cookies.Append(PlatformMiddleware.SessionCookie, session.Id, PlatformMiddleware.HostCookie());
        ctx.Response.Cookies.Delete(PlatformMiddleware.CsrfCookie, PlatformMiddleware.HostCookie());
        await audit.WriteStandaloneAsync(new AuditEntry(AppRegistry.PlatformSlug, user.Id, "auth.login", "session", null, AuditOutcomes.Success, idp.Name, null, null, Problems.CorrelationIdOf(ctx)), ct);
        return Results.Ok(new SessionResponse(true, user, session.CsrfToken, store.ExpiresAt(session)));
    }

    private static async Task<IResult> AppAccess(string slug, HttpContext ctx, StageGate gate, AuditWriter audit, CancellationToken ct)
    {
        if (ctx.Items[HttpItems.User] is not PlatformUser user)
        {
            return Problems.Result(ctx, 401, "Authentication required");
        }

        var result = await gate.EvaluateAsync(user, slug, ct);
        if (!result.Allowed)
        {
            await audit.WriteStandaloneAsync(new AuditEntry(result.Entry is null ? AppRegistry.PlatformSlug : slug, user.Id, "app.access", "app", slug, AuditOutcomes.Denied, result.Reason, null, null, Problems.CorrelationIdOf(ctx)), ct);
        }

        return Results.Ok(new AccessResponse(slug, result.Entry?.Stage, result.Allowed, result.Reason));
    }

    private static async Task<IResult> ListAudit(HttpContext ctx, IDbContextFactory<PlatformDbContext> factory, string? app, int? limit, long? beforeId, CancellationToken ct)
    {
        if (await Guard(ctx, "audit.read") is { } denied)
        {
            return denied;
        }

        var take = Math.Clamp(limit ?? 50, 1, 500);
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.AuditEvents.AsNoTracking();
        if (!string.IsNullOrEmpty(app))
        {
            q = q.Where(a => a.AppSlug == app);
        }

        if (beforeId is { } before)
        {
            q = q.Where(a => a.Id < before);
        }

        var rows = await q.OrderByDescending(a => a.Id).Take(take).ToListAsync(ct);
        return Results.Ok(rows.Select(r => new AuditEventDto(r.Id, r.Timestamp, r.AppSlug, r.ActorId, r.Action, r.ResourceType, r.ResourceId, r.Outcome, r.Reason, r.Before, r.After, r.CorrelationId, r.PrevHash, r.Hash)));
    }

    /// <summary>Authenticates and authorizes a platform action; returns a problem result on failure (denials audited).</summary>
    private static async Task<IResult?> Guard(HttpContext ctx, string action)
    {
        if (ctx.Items[HttpItems.User] is not PlatformUser user)
        {
            return Problems.Result(ctx, 401, "Authentication required");
        }

        var services = ctx.RequestServices;
        var subject = new AuthzSubject(user.Id, user.Roles.ToHashSet(StringComparer.Ordinal));
        var decision = ((IAuthorizer)services.GetService(typeof(IAuthorizer))!).Authorize(subject, AppRegistry.PlatformSlug, action, null);
        if (decision.Allowed)
        {
            return null;
        }

        var audit = (AuditWriter)services.GetService(typeof(AuditWriter))!;
        await audit.WriteStandaloneAsync(new AuditEntry(AppRegistry.PlatformSlug, user.Id, action, "platform", null, AuditOutcomes.Denied, decision.Reason, null, null, Problems.CorrelationIdOf(ctx)), ctx.RequestAborted);
        return Problems.Result(ctx, 403, "Access denied", decision.Reason);
    }
}
