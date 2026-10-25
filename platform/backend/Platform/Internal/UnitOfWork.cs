using System.Diagnostics;
using System.Text.Json;
using Barebone.Platform.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Options;

namespace Barebone.Platform.Internal;

internal sealed class IdempotencyState(string userId, string method, string path, string key, string requestHash)
{
    public string UserId { get; } = userId;
    public string Method { get; } = method;
    public string Path { get; } = path;
    public string Key { get; } = key;
    public string RequestHash { get; } = requestHash;
    public bool Stored { get; set; }
}

internal static class HttpItems
{
    public const string AppSlug = "Barebone.AppSlug";
    public const string User = "Barebone.User";
    public const string Session = "Barebone.Session";
    public const string Subject = "Barebone.Subject";
    public const string CorrelationId = "Barebone.CorrelationId";
    public const string Idempotency = "Barebone.Idempotency";
}

internal sealed class WriteScope(PlatformDbContext db) : IWriteScope
{
    internal PlatformDbContext Db { get; } = db;
    internal List<(string EventType, object Payload)> Outbox { get; } = [];
    internal string? ResourceId { get; private set; }
    internal string? SensitiveReadReason { get; private set; }

    public IQueryable<T> Query<T>() where T : class => Guard<T>().Set<T>();

    public ValueTask<T?> FindAsync<T>(object id, CancellationToken cancellationToken) where T : class =>
        Guard<T>().Set<T>().FindAsync([id], cancellationToken);

    public void Add<T>(T entity) where T : class => Guard<T>().Add(entity);

    public void Remove<T>(T entity) where T : class => Guard<T>().Remove(entity);

    public void Publish(string eventType, object payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentNullException.ThrowIfNull(payload);
        Outbox.Add((eventType, payload));
    }

    public void SetResourceId(string resourceId) => ResourceId = resourceId;

    public void MarkAsSensitiveRead(string reason) => SensitiveReadReason = string.IsNullOrWhiteSpace(reason) ? "sensitive-read" : reason;

    private PlatformDbContext Guard<T>()
    {
        if (PlatformDbContext.PlatformInternalTypes.Contains(typeof(T)) || typeof(T) == typeof(CatalogRow))
        {
            throw new InvalidOperationException("Platform tables are not accessible through IWriteScope.");
        }

        return Db;
    }
}

internal sealed class AppData(PlatformDbContext db) : IAppData
{
    public IQueryable<T> Query<T>() where T : class
    {
        if (PlatformDbContext.PlatformInternalTypes.Contains(typeof(T)) || typeof(T) == typeof(CatalogRow))
        {
            throw new InvalidOperationException("Platform tables are not accessible through IAppData.");
        }

        return db.Set<T>().AsNoTracking();
    }
}

internal static class AppSlugOverride
{
    public static readonly AsyncLocal<string?> Current = new();
}

internal sealed class UnitOfWork(
    IDbContextFactory<PlatformDbContext> factory,
    WriteLock writeLock,
    AuditWriter audit,
    AppRegistry registry,
    ICurrentUser currentUser,
    IHttpContextAccessor httpContextAccessor,
    TimeProvider time,
    IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions) : IUnitOfWork
{
    public async Task<TResult> ExecuteAsync<TResult>(
        UnitOfWorkContext context,
        Func<IWriteScope, CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(work);
        var http = httpContextAccessor.HttpContext;
        var actor = http is null ? "system" : currentUser.User?.Id ?? "anonymous";
        var appSlug = AppSlugOverride.Current.Value ?? http?.Items[HttpItems.AppSlug] as string ?? AppRegistry.PlatformSlug;

        using var held = await writeLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var scope = new WriteScope(db);

        var result = await work(scope, cancellationToken).ConfigureAwait(false);

        var changes = CaptureChanges(db);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var before = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        var after = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            var name = change.Entry.Metadata.ClrType.Name + ":" + (change.KeyBeforeSave ?? KeyOf(change.Entry));
            if (change.Before is not null)
            {
                before[name] = change.Before;
            }

            if (change.State != EntityState.Deleted)
            {
                after[name] = change.State == EntityState.Added ? Snapshot(change.Entry, null, current: true) : change.After!;
            }
        }

        var resourceId = scope.ResourceId ?? context.ResourceId
            ?? (changes.Count(c => c.State == EntityState.Added) == 1 ? KeyOf(changes.First(c => c.State == EntityState.Added).Entry) : null);

        await audit.AppendAsync(db, new AuditEntry(
            appSlug,
            actor,
            context.Action,
            context.ResourceType,
            resourceId,
            scope.SensitiveReadReason is null ? AuditOutcomes.Success : AuditOutcomes.Read,
            scope.SensitiveReadReason,
            AuditJson.Diff(before),
            AuditJson.Diff(after),
            currentUser.CorrelationId), cancellationToken).ConfigureAwait(false);

        var now = time.GetUtcNow();
        if (http?.Items[HttpItems.Idempotency] is IdempotencyState { Stored: false } idem)
        {
            db.IdempotencyRecords.Add(new IdempotencyRecordRow
            {
                UserId = idem.UserId,
                Method = idem.Method,
                Path = idem.Path,
                Key = idem.Key,
                RequestHash = idem.RequestHash,
                StatusCode = StatusCodes.Status200OK,
                ResponseBody = JsonSerializer.Serialize(result, result?.GetType() ?? typeof(TResult), jsonOptions.Value.SerializerOptions),
                CreatedAt = now,
            });
            idem.Stored = true;
        }

        foreach (var (eventType, payload) in scope.Outbox)
        {
            db.OutboxMessages.Add(new OutboxRow
            {
                AppSlug = appSlug,
                EventType = eventType,
                PayloadJson = JsonSerializer.Serialize(payload, payload.GetType(), jsonOptions.Value.SerializerOptions),
                CorrelationId = currentUser.CorrelationId,
                CreatedAt = now,
            });
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (http?.Items[HttpItems.Idempotency] is IdempotencyState stored)
            {
                stored.Stored = false;
            }

            throw;
        }

        Activity.Current?.AddEvent(new ActivityEvent("barebone.uow.committed"));
        return result;
    }

    private sealed record Change(EntityEntry Entry, EntityState State, Dictionary<string, object?>? Before, Dictionary<string, object?>? After, string? KeyBeforeSave);

    private List<Change> CaptureChanges(PlatformDbContext db)
    {
        db.ChangeTracker.DetectChanges();
        var list = new List<Change>();
        foreach (var entry in db.ChangeTracker.Entries())
        {
            if (PlatformDbContext.PlatformInternalTypes.Contains(entry.Metadata.ClrType))
            {
                continue;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    list.Add(new Change(entry, EntityState.Added, null, null, null));
                    break;
                case EntityState.Modified:
                    var changed = entry.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name).ToHashSet(StringComparer.Ordinal);
                    list.Add(new Change(entry, EntityState.Modified, Snapshot(entry, changed, current: false), Snapshot(entry, changed, current: true), KeyOf(entry)));
                    break;
                case EntityState.Deleted:
                    list.Add(new Change(entry, EntityState.Deleted, Snapshot(entry, null, current: false), null, KeyOf(entry)));
                    break;
                default:
                    break;
            }
        }

        return list;
    }

    private Dictionary<string, object?> Snapshot(EntityEntry entry, HashSet<string>? only, bool current)
    {
        var pii = registry.Get(entry.Metadata.FindAnnotation(PlatformDbContext.OwnerAnnotation)?.Value as string ?? string.Empty)?.Manifest.PiiFields ?? [];
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in entry.Properties)
        {
            var name = p.Metadata.Name;
            if (only is not null && !only.Contains(name))
            {
                continue;
            }

            if (p.Metadata.IsShadowProperty())
            {
                continue;
            }

            var value = current ? p.CurrentValue : p.OriginalValue;
            values[JsonNamingPolicy.CamelCase.ConvertName(name)] = AuditJson.IsPii(entry.Metadata.ClrType, name, pii) && value is not null ? "***" : value;
        }

        return values;
    }

    private static string? KeyOf(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        return key is null ? null : string.Join("|", key.Properties.Select(p => Convert.ToString(entry.Property(p.Name).CurrentValue, System.Globalization.CultureInfo.InvariantCulture)));
    }
}
