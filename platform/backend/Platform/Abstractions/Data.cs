namespace Barebone.Platform.Abstractions;

public sealed record UnitOfWorkContext(string Action, string ResourceType, string? ResourceId = null);

/// <summary>The only write path for app code. Business change + audit + idempotency + outbox commit together or not at all.</summary>
public interface IUnitOfWork
{
    Task<TResult> ExecuteAsync<TResult>(
        UnitOfWorkContext context,
        Func<IWriteScope, CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken = default);
}

/// <summary>Write access inside one transaction. Never exposes SaveChanges.</summary>
public interface IWriteScope
{
    /// <summary>Tracked query; changes to returned entities are saved and audited at commit.</summary>
    IQueryable<T> Query<T>() where T : class;
    ValueTask<T?> FindAsync<T>(object id, CancellationToken cancellationToken) where T : class;
    void Add<T>(T entity) where T : class;
    void Remove<T>(T entity) where T : class;

    /// <summary>Queue an outbox message, committed with the transaction.</summary>
    void Publish(string eventType, object payload);

    /// <summary>Set the audited resource id (e.g. after creating an entity).</summary>
    void SetResourceId(string resourceId);

    /// <summary>Mark this unit of work's audit row as a sensitive read (outcome "read"), e.g. a PII reveal.</summary>
    void MarkAsSensitiveRead(string reason);
}

/// <summary>Read-only, no-tracking access for app code.</summary>
public interface IAppData
{
    IQueryable<T> Query<T>() where T : class;
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class PiiAttribute : Attribute;
