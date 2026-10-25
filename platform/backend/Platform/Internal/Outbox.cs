using System.Text.Json;
using System.Text.Json.Nodes;
using Barebone.Platform.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Barebone.Platform.Internal;

internal sealed class OutboxRelay(
    IDbContextFactory<PlatformDbContext> factory,
    IOutboxPublisher publisher,
    WriteLock writeLock,
    TimeProvider time,
    ILogger<OutboxRelay> logger) : IOutboxRelay
{
    public async Task<int> RelayPendingAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var pending = await db.OutboxMessages.AsNoTracking().Where(o => o.SentAt == null).OrderBy(o => o.Id).Take(100)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var published = 0;
        foreach (var row in pending)
        {
            int claimed;
            using (await writeLock.AcquireAsync(cancellationToken).ConfigureAwait(false))
            {
                var now = time.GetUtcNow();
                claimed = await db.OutboxMessages.Where(o => o.Id == row.Id && o.SentAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.SentAt, now), cancellationToken).ConfigureAwait(false);
            }

            if (claimed != 1)
            {
                continue;
            }

            try
            {
                await publisher.PublishAsync(new OutboxMessage(row.Id, row.AppSlug, row.EventType, row.PayloadJson, row.CorrelationId, row.CreatedAt), cancellationToken).ConfigureAwait(false);
                published++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Outbox publish failed for message {OutboxId}; will retry. Error type {ErrorType}", row.Id, ex.GetType().Name);
                using (await writeLock.AcquireAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    await db.OutboxMessages.Where(o => o.Id == row.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(o => o.SentAt, (DateTimeOffset?)null), CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        return published;
    }
}

/// <summary>Default publisher: structured log line with a redacted payload (stand-in for a message bus).</summary>
internal sealed class LogOutboxPublisher(ILogger<LogOutboxPublisher> logger) : IOutboxPublisher
{
    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!logger.IsEnabled(LogLevel.Information))
        {
            return Task.CompletedTask;
        }

        logger.LogInformation(
            "Outbox message {OutboxId} {EventType} from {AppSlug} published (correlation {CorrelationId}): {Payload}",
            message.Id, message.EventType, message.AppSlug, message.CorrelationId, LogRedaction.RedactJson(message.PayloadJson));
        return Task.CompletedTask;
    }
}

internal sealed class OutboxRelayService(IOutboxRelay relay, IConfiguration configuration, ILogger<OutboxRelayService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = configuration.GetValue("Platform:Outbox:RelayIntervalSeconds", 2.0);
        if (seconds <= 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await relay.RelayPendingAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Outbox relay iteration failed: {ErrorType}", ex.GetType().Name);
            }
        }
    }
}

internal static class LogRedaction
{
    private static readonly string[] SensitiveFragments =
    [
        "password", "secret", "token", "cookie", "authorization", "apikey", "api_key", "key", "session", "csrf",
        "ssn", "dateofbirth", "dob", "customername", "email", "pan", "card",
    ];

    public static bool IsSensitiveKey(string name)
    {
        var n = name.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return SensitiveFragments.Any(f => n.Contains(f.Replace("_", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal));
    }

    public static string RedactJson(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            Redact(node);
            return node?.ToJsonString() ?? "null";
        }
        catch (JsonException)
        {
            return "\"[unparseable payload redacted]\"";
        }
    }

    private static void Redact(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    if (IsSensitiveKey(name))
                    {
                        obj[name] = "***";
                    }
                    else
                    {
                        Redact(obj[name]);
                    }
                }

                break;
            case JsonArray arr:
                foreach (var item in arr)
                {
                    Redact(item);
                }

                break;
            default:
                break;
        }
    }
}
