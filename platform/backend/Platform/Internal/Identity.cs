using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Barebone.Platform.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Barebone.Platform.Internal;

internal sealed class DevIdentityProvider(IConfiguration configuration) : IIdentityProvider
{
    private readonly Lazy<IReadOnlyList<PlatformUser>> users = new(() => Load(configuration));

    public string Name => "dev";

    public Task<IReadOnlyList<PlatformUser>> ListUsersAsync(CancellationToken cancellationToken) => Task.FromResult(users.Value);

    public Task<PlatformUser?> FindUserAsync(string userId, CancellationToken cancellationToken) =>
        Task.FromResult(users.Value.FirstOrDefault(u => string.Equals(u.Id, userId, StringComparison.Ordinal)));

    private static IReadOnlyList<PlatformUser> Load(IConfiguration configuration)
    {
        var path = configuration["Platform:DevDirectoryPath"] is { Length: > 0 } p
            ? p
            : Path.Combine(AppContext.BaseDirectory, "config", "dev-directory.json");
        using var stream = File.OpenRead(path);
        var doc = JsonSerializer.Deserialize<DirectoryDoc>(stream, WebJson)
            ?? throw new InvalidOperationException("dev-directory.json is empty");
        return [.. (doc.Users ?? []).Select(u => new PlatformUser(u.Id!, u.DisplayName!, u.Email!, u.Roles ?? []))];
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private sealed record DirectoryDoc(List<DirectoryUser>? Users);

    private sealed record DirectoryUser(string? Id, string? DisplayName, string? Email, List<string>? Roles);
}

internal sealed class SessionOptions
{
    public double IdleMinutes { get; set; } = 30;
    public double AbsoluteHours { get; set; } = 8;
}

internal sealed class SessionData(string id, string userId, string csrfToken, DateTimeOffset createdAt)
{
    public string Id { get; } = id;
    public string UserId { get; } = userId;
    public string CsrfToken { get; } = csrfToken;
    public DateTimeOffset CreatedAt { get; } = createdAt;
    public DateTimeOffset LastSeen { get; set; } = createdAt;
}

internal interface ISessionStore
{
    SessionData Create(string userId);

    /// <summary>Returns the live session (touching its idle timer) or null if unknown/expired.</summary>
    SessionData? Get(string sessionId);

    void Remove(string sessionId);

    DateTimeOffset ExpiresAt(SessionData session);
}

internal sealed class InMemorySessionStore(TimeProvider time, IOptions<SessionOptions> options) : ISessionStore
{
    private readonly ConcurrentDictionary<string, SessionData> sessions = new(StringComparer.Ordinal);

    public SessionData Create(string userId)
    {
        var session = new SessionData(Tokens.NewToken(), userId, Tokens.NewToken(), time.GetUtcNow());
        sessions[Hash(session.Id)] = session;
        return session;
    }

    public SessionData? Get(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || !sessions.TryGetValue(Hash(sessionId), out var session))
        {
            return null;
        }

        var now = time.GetUtcNow();
        if (now >= ExpiresAt(session))
        {
            sessions.TryRemove(Hash(sessionId), out _);
            return null;
        }

        session.LastSeen = now;
        return session;
    }

    public void Remove(string sessionId) => sessions.TryRemove(Hash(sessionId), out _);

    public DateTimeOffset ExpiresAt(SessionData session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var idle = session.LastSeen.AddMinutes(options.Value.IdleMinutes);
        var absolute = session.CreatedAt.AddHours(options.Value.AbsoluteHours);
        return idle < absolute ? idle : absolute;
    }

    private static string Hash(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
}

internal static class Tokens
{
    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static bool FixedTimeEquals(string? a, string? b) =>
        a is not null && b is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private string? fallbackCorrelationId;

    public bool IsAuthenticated => User is not null;

    public PlatformUser? User => accessor.HttpContext?.Items[HttpItems.User] as PlatformUser;

    public PlatformUser RequiredUser => User ?? throw new InvalidOperationException("No authenticated user.");

    public AuthzSubject Subject =>
        accessor.HttpContext?.Items[HttpItems.Subject] as AuthzSubject
        ?? (User is { } u ? new AuthzSubject(u.Id, u.Roles.ToHashSet(StringComparer.Ordinal)) : new AuthzSubject(string.Empty, new HashSet<string>()));

    public string CorrelationId =>
        accessor.HttpContext?.Items[HttpItems.CorrelationId] as string
        ?? (fallbackCorrelationId ??= Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"));
}
