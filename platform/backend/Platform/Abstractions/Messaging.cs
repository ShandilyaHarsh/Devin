namespace Barebone.Platform.Abstractions;

public sealed record OutboxMessage(long Id, string AppSlug, string EventType, string PayloadJson, string CorrelationId, DateTimeOffset CreatedAt);

/// <summary>Seam for the message bus (log sink now; Service Bus later).</summary>
public interface IOutboxPublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken);
}

public interface IOutboxRelay
{
    /// <summary>Publishes committed, unsent outbox rows once each; safe to run concurrently. Returns rows published by this call.</summary>
    Task<int> RelayPendingAsync(CancellationToken cancellationToken);
}

public static class PiiMask
{
    public static string Name(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => p[0] + "***"));

    public static string Last4(string? last4) =>
        "***-**-" + (last4 is { Length: 4 } ? last4 : "****");

    public static string Date(DateOnly? _) => "****-**-**";

    public static string Email(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains('@', StringComparison.Ordinal))
        {
            return "***";
        }

        var at = value.IndexOf('@', StringComparison.Ordinal);
        return value[0] + "***" + value[at..];
    }
}
