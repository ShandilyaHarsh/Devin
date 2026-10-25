namespace Barebone.Platform.Abstractions;

/// <summary>
/// A business/platform rule rejected the request. Mapped to problem JSON with the given status (e.g. 409, 422) and
/// title; rolls back the surrounding UnitOfWork. The title is shown to the caller, so it must not contain sensitive data.
/// </summary>
public sealed class PlatformRuleViolationException(int statusCode, string title, string? reason = null) : Exception(title)
{
    public int StatusCode { get; } = statusCode;
    public string Title { get; } = title;
    public string? Reason { get; } = reason;
}
