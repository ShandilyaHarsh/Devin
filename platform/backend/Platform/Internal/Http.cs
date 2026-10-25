using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Barebone.Platform.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Barebone.Platform.Internal;

internal sealed record ProblemBody(
    string Type,
    string Title,
    int Status,
    string CorrelationId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail = null);

internal static class Problems
{
    public static string CorrelationIdOf(HttpContext ctx) =>
        ctx.Items[HttpItems.CorrelationId] as string ?? ctx.TraceIdentifier;

    public static IResult Result(HttpContext ctx, int status, string title, string? reason = null, string? detail = null) =>
        Results.Json(new ProblemBody("about:blank", title, status, CorrelationIdOf(ctx), reason, detail), statusCode: status, contentType: "application/problem+json");

    public static Task WriteAsync(HttpContext ctx, int status, string title, string? reason = null, string? detail = null) =>
        Result(ctx, status, title, reason, detail).ExecuteAsync(ctx);

    public static string TitleFor(int status) => status switch
    {
        400 => "Bad request",
        401 => "Authentication required",
        403 => "Access denied",
        404 => "Not found",
        405 => "Method not allowed",
        409 => "Conflict",
        415 => "Unsupported media type",
        422 => "Unprocessable request",
        429 => "Too many requests",
        _ => status >= 500 ? "An unexpected error occurred" : "Request failed",
    };
}

internal static class Methods
{
    public static bool IsSafe(string method) => HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
}

internal static partial class PlatformMiddleware
{
    public const string SessionCookie = "__Host-session";
    public const string CsrfCookie = "__Host-csrf";
    public const string CsrfHeader = "X-CSRF-Token";
    public const string IdempotencyHeader = "Idempotency-Key";

    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex CorrelationRegex();

    [GeneratedRegex("^[A-Za-z0-9-]{8,128}$")]
    private static partial Regex IdempotencyKeyRegex();

    public static CookieOptions HostCookie() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true,
    };

    public static Task SecurityHeaders(HttpContext ctx, RequestDelegate next)
    {
        ctx.Response.OnStarting(() =>
        {
            var h = ctx.Response.Headers;
            h.ContentSecurityPolicy = ContentSecurityPolicy;
            h.StrictTransportSecurity = "max-age=31536000; includeSubDomains";
            h.XContentTypeOptions = "nosniff";
            h["Referrer-Policy"] = "no-referrer";
            h.XFrameOptions = "DENY";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                h.CacheControl = "no-store";
            }

            h.Remove("Server");
            return Task.CompletedTask;
        });
        return next(ctx);
    }

    public static async Task CorrelationId(HttpContext ctx, RequestDelegate next)
    {
        var incoming = ctx.Request.Headers["X-Correlation-Id"].ToString();
        var id = CorrelationRegex().IsMatch(incoming) ? incoming : Guid.NewGuid().ToString();
        ctx.Items[HttpItems.CorrelationId] = id;
        ctx.TraceIdentifier = id;
        Activity.Current?.SetTag("correlation.id", id);
        ctx.Response.OnStarting(() =>
        {
            ctx.Response.Headers["X-Correlation-Id"] = id;
            return Task.CompletedTask;
        });
        var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Barebone.Platform.Request");
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
        {
            await next(ctx).ConfigureAwait(false);
        }
    }

    public static async Task Session(HttpContext ctx, RequestDelegate next)
    {
        if (ctx.Request.Cookies.TryGetValue(SessionCookie, out var sid) && !string.IsNullOrEmpty(sid))
        {
            var store = ctx.RequestServices.GetRequiredService<ISessionStore>();
            if (store.Get(sid) is { } session)
            {
                var user = await ctx.RequestServices.GetRequiredService<IIdentityProvider>()
                    .FindUserAsync(session.UserId, ctx.RequestAborted).ConfigureAwait(false);
                if (user is null)
                {
                    store.Remove(sid);
                }
                else
                {
                    ctx.Items[HttpItems.Session] = session;
                    ctx.Items[HttpItems.User] = user;
                    Activity.Current?.SetTag("enduser.id", user.Id);
                }
            }
        }

        await next(ctx).ConfigureAwait(false);
    }

    public static Task Csrf(HttpContext ctx, RequestDelegate next)
    {
        if (Methods.IsSafe(ctx.Request.Method))
        {
            return next(ctx);
        }

        var expected = (ctx.Items[HttpItems.Session] as SessionData)?.CsrfToken
            ?? (ctx.Request.Cookies.TryGetValue(CsrfCookie, out var pre) ? pre : null);
        var provided = ctx.Request.Headers[CsrfHeader].ToString();
        return Tokens.FixedTimeEquals(expected, provided)
            ? next(ctx)
            : Problems.WriteAsync(ctx, StatusCodes.Status403Forbidden, "CSRF token missing or invalid", "csrf");
    }

    /// <summary>
    /// Platform filters for /api/apps/{slug}: authenticated (401) -> stage gate (403, audited) -> RequireAction
    /// (403, audited; missing => 403; loader null => 404) -> idempotency (mutations) -> handler.
    /// Runs as endpoint-aware middleware so it executes before request-body binding.
    /// </summary>
    public static async Task AppFilters(HttpContext ctx, RequestDelegate next)
    {
        var endpoint = ctx.GetEndpoint();
        var app = endpoint?.Metadata.GetMetadata<AppEndpointMetadata>();
        if (endpoint is null || app is null)
        {
            await next(ctx).ConfigureAwait(false);
            return;
        }

        var services = ctx.RequestServices;
        ctx.Items[HttpItems.AppSlug] = app.Slug;
        if (ctx.Items[HttpItems.User] is not PlatformUser user)
        {
            await Problems.WriteAsync(ctx, StatusCodes.Status401Unauthorized, "Authentication required").ConfigureAwait(false);
            return;
        }

        var audit = services.GetRequiredService<AuditWriter>();
        var correlationId = Problems.CorrelationIdOf(ctx);
        var gate = await services.GetRequiredService<StageGate>().EvaluateAsync(user, app.Slug, ctx.RequestAborted).ConfigureAwait(false);
        if (!gate.Allowed)
        {
            await audit.WriteStandaloneAsync(new AuditEntry(app.Slug, user.Id, "app.access", "app", app.Slug, AuditOutcomes.Denied, gate.Reason, null, null, correlationId), ctx.RequestAborted).ConfigureAwait(false);
            await Problems.WriteAsync(ctx, StatusCodes.Status403Forbidden, "Access denied", gate.Reason).ConfigureAwait(false);
            return;
        }

        ctx.Items[HttpItems.Subject] = gate.Subject;
        var required = endpoint.Metadata.GetMetadata<RequiredActionMetadata>();
        if (required is null)
        {
            await audit.WriteStandaloneAsync(new AuditEntry(app.Slug, user.Id, "endpoint.invoke", "endpoint", ctx.Request.Path, AuditOutcomes.Denied, "no-required-action", null, null, correlationId), ctx.RequestAborted).ConfigureAwait(false);
            await Problems.WriteAsync(ctx, StatusCodes.Status403Forbidden, "Access denied", "no-required-action").ConfigureAwait(false);
            return;
        }

        ResourceRef? resource = null;
        if (required.ResourceLoader is not null)
        {
            resource = await required.ResourceLoader(ctx, ctx.RequestAborted).ConfigureAwait(false);
            if (resource is null)
            {
                await Problems.WriteAsync(ctx, StatusCodes.Status404NotFound, "Not found").ConfigureAwait(false);
                return;
            }
        }

        var decision = services.GetRequiredService<IAuthorizer>().Authorize(gate.Subject!, app.Slug, required.Action, resource);
        if (!decision.Allowed)
        {
            await audit.WriteStandaloneAsync(new AuditEntry(app.Slug, user.Id, required.Action, resource?.Type ?? "endpoint", resource?.Id, AuditOutcomes.Denied, decision.Reason, null, null, correlationId), ctx.RequestAborted).ConfigureAwait(false);
            await Problems.WriteAsync(ctx, StatusCodes.Status403Forbidden, "Access denied", decision.Reason).ConfigureAwait(false);
            return;
        }

        if (Methods.IsSafe(ctx.Request.Method))
        {
            await next(ctx).ConfigureAwait(false);
            return;
        }

        await Idempotent(ctx, user, next).ConfigureAwait(false);
    }

    private static async Task Idempotent(HttpContext ctx, PlatformUser user, RequestDelegate next)
    {
        var key = ctx.Request.Headers[IdempotencyHeader];
        if (key.Count != 1 || !IdempotencyKeyRegex().IsMatch(key.ToString()))
        {
            await Problems.WriteAsync(ctx, StatusCodes.Status400BadRequest, "Idempotency-Key header missing or invalid", "idempotency-key").ConfigureAwait(false);
            return;
        }

        ctx.Request.EnableBuffering();
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(ctx.Request.Body, ctx.RequestAborted).ConfigureAwait(false));
        ctx.Request.Body.Position = 0;

        var state = new IdempotencyState(user.Id, ctx.Request.Method.ToUpperInvariant(), (ctx.Request.PathBase + ctx.Request.Path).Value!.ToLowerInvariant(), key.ToString(), hash);
        var inFlight = ctx.RequestServices.GetRequiredService<IdempotencyInFlight>();
        var scopeKey = string.Join('\n', state.UserId, state.Method, state.Path, state.Key);
        if (!inFlight.TryEnter(scopeKey))
        {
            await Problems.WriteAsync(ctx, StatusCodes.Status409Conflict, "A request with this Idempotency-Key is already in progress", "idempotency-in-flight").ConfigureAwait(false);
            return;
        }

        try
        {
            await using (var db = await ctx.RequestServices.GetRequiredService<IDbContextFactory<PlatformDbContext>>().CreateDbContextAsync(ctx.RequestAborted).ConfigureAwait(false))
            {
                var existing = await db.IdempotencyRecords.AsNoTracking()
                    .FirstOrDefaultAsync(r => r.UserId == state.UserId && r.Method == state.Method && r.Path == state.Path && r.Key == state.Key, ctx.RequestAborted)
                    .ConfigureAwait(false);
                if (existing is not null)
                {
                    if (!string.Equals(existing.RequestHash, hash, StringComparison.Ordinal))
                    {
                        await Problems.WriteAsync(ctx, StatusCodes.Status422UnprocessableEntity, "Idempotency key reused with a different request", "idempotency-mismatch").ConfigureAwait(false);
                        return;
                    }

                    ctx.Response.StatusCode = existing.StatusCode;
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    ctx.Response.Headers["Idempotency-Replayed"] = "true";
                    await ctx.Response.WriteAsync(existing.ResponseBody, ctx.RequestAborted).ConfigureAwait(false);
                    return;
                }
            }

            ctx.Items[HttpItems.Idempotency] = state;
            await next(ctx).ConfigureAwait(false);
        }
        finally
        {
            inFlight.Exit(scopeKey);
        }
    }

    public static async Task HandleException(HttpContext ctx)
    {
        var feature = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
        var ex = feature?.Error;
        var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Barebone.Platform.Errors");
        switch (ex)
        {
            case PlatformRuleViolationException rule:
                await Problems.WriteAsync(ctx, rule.StatusCode, rule.Title, rule.Reason).ConfigureAwait(false);
                break;
            case ManifestValidationException manifest:
                await Problems.WriteAsync(ctx, StatusCodes.Status422UnprocessableEntity, "Invalid manifest", "invalid-manifest", string.Join("; ", manifest.Errors)).ConfigureAwait(false);
                break;
            case BadHttpRequestException bad:
                await Problems.WriteAsync(ctx, bad.StatusCode, Problems.TitleFor(bad.StatusCode)).ConfigureAwait(false);
                break;
            default:
                logger.LogError("Unhandled {ErrorType} for {Method} {Path}", ex?.GetType().FullName, ctx.Request.Method, ctx.Request.Path.Value);
                await Problems.WriteAsync(ctx, StatusCodes.Status500InternalServerError, "An unexpected error occurred").ConfigureAwait(false);
                break;
        }
    }
}

internal sealed record AppEndpointMetadata(string Slug);

internal sealed class IdempotencyInFlight
{
    private readonly ConcurrentDictionary<string, byte> keys = new(StringComparer.Ordinal);

    public bool TryEnter(string key) => keys.TryAdd(key, 0);

    public void Exit(string key) => keys.TryRemove(key, out _);
}
