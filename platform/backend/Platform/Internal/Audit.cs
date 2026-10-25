using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Barebone.Platform.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Barebone.Platform.Internal;

/// <summary>Process-wide write lock; every write transaction (UoW, standalone audit, outbox claim) holds it.</summary>
internal sealed class WriteLock : IDisposable
{
    private readonly SemaphoreSlim semaphore = new(1, 1);
    private static readonly AsyncLocal<bool> Held = new();

    public static bool IsHeldByCurrentFlow => Held.Value;

    public void Dispose() => semaphore.Dispose();

    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        if (Held.Value)
        {
            throw new InvalidOperationException("Nested write transactions are not supported.");
        }

        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        Held.Value = true;
        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                Held.Value = false;
                semaphore.Release();
            }
        }
    }
}

internal static class SecretChecks
{
    private static readonly string[] Placeholders = ["changeme", "change-me", "placeholder", "todo", "xxx", "secret", "example", "default"];

    public static byte[] RequireAuditChainKey(IConfiguration configuration)
    {
        var key = configuration["Platform:AuditChainKey"];
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("Required secret Platform:AuditChainKey is not configured (set env Platform__AuditChainKey or dotnet user-secrets).");
        }

        var lower = key.ToLowerInvariant();
        if (key.Length < 32 || Placeholders.Any(p => lower.Contains(p, StringComparison.Ordinal)) || (key.Contains('<', StringComparison.Ordinal) && key.Contains('>', StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Platform:AuditChainKey must be at least 32 characters of random data and not a placeholder.");
        }

        return Encoding.UTF8.GetBytes(key);
    }
}

internal sealed class AuditChain(IConfiguration configuration)
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private readonly byte[] key = SecretChecks.RequireAuditChainKey(configuration);

    public string ComputeHash(AuditEventRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        string?[] fields =
        [
            row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), row.Timestamp, row.AppSlug, row.ActorId,
            row.Action, row.ResourceType, row.ResourceId, row.Outcome, row.Reason, row.Before, row.After, row.CorrelationId,
        ];
        var canonical = JsonSerializer.Serialize(fields) + row.PrevHash;
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical)));
    }
}

internal sealed record AuditEntry(
    string AppSlug,
    string ActorId,
    string Action,
    string ResourceType,
    string? ResourceId,
    string Outcome,
    string? Reason,
    string? Before,
    string? After,
    string CorrelationId);

/// <summary>Append-only writer. There is deliberately no update or delete code path for audit rows.</summary>
internal sealed class AuditWriter(AuditChain chain, TimeProvider time, IDbContextFactory<PlatformDbContext> factory, WriteLock writeLock)
{
    /// <summary>Adds the next chained row to <paramref name="db"/>. Caller must hold the write lock and an open transaction.</summary>
    public async Task<AuditEventRow> AppendAsync(PlatformDbContext db, AuditEntry entry, CancellationToken cancellationToken)
    {
        var last = await db.AuditEvents.AsNoTracking().OrderByDescending(a => a.Id)
            .Select(a => new { a.Id, a.Hash }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var row = new AuditEventRow
        {
            Id = (last?.Id ?? 0) + 1,
            Timestamp = time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            AppSlug = entry.AppSlug,
            ActorId = entry.ActorId,
            Action = entry.Action,
            ResourceType = entry.ResourceType,
            ResourceId = entry.ResourceId,
            Outcome = entry.Outcome,
            Reason = entry.Reason,
            Before = entry.Before,
            After = entry.After,
            CorrelationId = entry.CorrelationId,
            PrevHash = last?.Hash ?? AuditChain.GenesisHash,
        };
        row.Hash = chain.ComputeHash(row);
        db.AuditEvents.Add(row);
        return row;
    }

    /// <summary>Writes a single audit row in its own transaction (denials, logins, reads outside a UoW).</summary>
    public async Task WriteStandaloneAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        using var held = await writeLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AppendAsync(db, entry, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record AuditVerifyResult(bool Valid, int Checked, long? FirstBrokenId, string? Reason);

internal sealed class AuditVerifier(AuditChain chain, IDbContextFactory<PlatformDbContext> factory)
{
    public async Task<AuditVerifyResult> VerifyAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var prevHash = AuditChain.GenesisHash;
        long expectedId = 1;
        var count = 0;
        await foreach (var row in db.AuditEvents.AsNoTracking().OrderBy(a => a.Id).AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            count++;
            if (row.Id != expectedId)
            {
                return new AuditVerifyResult(false, count, row.Id, $"sequence gap: expected id {expectedId}");
            }

            if (!string.Equals(row.PrevHash, prevHash, StringComparison.Ordinal))
            {
                return new AuditVerifyResult(false, count, row.Id, "prevHash does not match the previous row's hash");
            }

            var expected = chain.ComputeHash(row);
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(row.Hash ?? string.Empty)))
            {
                return new AuditVerifyResult(false, count, row.Id, "hash mismatch: row content was modified");
            }

            prevHash = row.Hash!;
            expectedId++;
        }

        return new AuditVerifyResult(true, count, null, null);
    }
}

internal static class AuditOutcomes
{
    public const string Success = "success";
    public const string Denied = "denied";
    public const string Read = "read";
}

internal static class AuditJson
{
    public static string? Diff(IReadOnlyDictionary<string, Dictionary<string, object?>> values) =>
        values.Count == 0 ? null : JsonSerializer.Serialize(values);

    public static bool IsPii(Type clrType, string property, IReadOnlyCollection<string> manifestPii) =>
        manifestPii.Contains(property, StringComparer.OrdinalIgnoreCase)
        || clrType.GetProperty(property)?.GetCustomAttributes(typeof(PiiAttribute), true).Length > 0;
}
