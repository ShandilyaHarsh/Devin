namespace Barebone.Platform.Abstractions;

/// <summary>Attributes of the resource being accessed, used by policy conditions (resource.X).</summary>
public sealed record ResourceRef(string Type, string? Id, IReadOnlyDictionary<string, object?> Attributes)
{
    public static ResourceRef Of(string type, string? id, IReadOnlyDictionary<string, object?>? attributes = null) =>
        new(type, id, attributes ?? new Dictionary<string, object?>());
}

public sealed record AuthzDecision(bool Allowed, string Reason, string? MatchedRule);

/// <summary>Deny-by-default policy decision point. Pure and in-memory (Cerbos later).</summary>
public interface IAuthorizer
{
    AuthzDecision Authorize(AuthzSubject subject, string appSlug, string action, ResourceRef? resource);
}

public static class PseudoRoles
{
    public const string Builder = "platform:builder";
    public const string DemoViewer = "platform:demo-viewer";
}
