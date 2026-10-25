using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Barebone.Platform.Internal;

/// <summary>JSON console formatter that redacts structured values whose names look sensitive.</summary>
internal sealed partial class RedactingJsonFormatter() : ConsoleFormatter(Name)
{
    public new const string Name = "barebone-json";

    [GeneratedRegex(@"\{([A-Za-z0-9_@]+)(?:[:,][^}]*)?\}")]
    private static partial Regex HoleRegex();

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        ArgumentNullException.ThrowIfNull(textWriter);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        string message;
        if (logEntry.State is IReadOnlyList<KeyValuePair<string, object?>> kvs)
        {
            string? format = null;
            foreach (var (key, value) in kvs)
            {
                if (key == "{OriginalFormat}")
                {
                    format = value as string;
                    continue;
                }

                values[key] = LogRedaction.IsSensitiveKey(key) ? "***" : value?.ToString();
            }

            message = format is null
                ? logEntry.Formatter(logEntry.State, logEntry.Exception)
                : HoleRegex().Replace(format, m => values.TryGetValue(m.Groups[1].Value.TrimStart('@'), out var v) ? v?.ToString() ?? "null" : m.Value);
        }
        else
        {
            message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        }

        scopeProvider?.ForEachScope((scope, state) =>
        {
            if (scope is IEnumerable<KeyValuePair<string, object>> pairs)
            {
                foreach (var (key, value) in pairs)
                {
                    if (key is "CorrelationId" or "RequestPath" or "RequestId")
                    {
                        state[key] = LogRedaction.IsSensitiveKey(key) ? "***" : value?.ToString();
                    }
                }
            }
        }, values);

        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["level"] = logEntry.LogLevel.ToString(),
            ["category"] = logEntry.Category,
            ["message"] = message,
            ["traceId"] = Activity.Current?.TraceId.ToString(),
            ["exception"] = logEntry.Exception?.GetType().FullName,
            ["state"] = values,
        };
        textWriter.WriteLine(JsonSerializer.Serialize(record, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}

