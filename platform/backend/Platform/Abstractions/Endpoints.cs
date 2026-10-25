using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Barebone.Platform.Abstractions;

public delegate ValueTask<ResourceRef?> ResourceLoader(HttpContext httpContext, CancellationToken cancellationToken);

public static class EndpointAuthorizationExtensions
{
    internal const string ActionMetadataKey = "Barebone.Platform.Action";

    /// <summary>
    /// Declares the policy action for an endpoint. The platform evaluates it (deny by default) before the handler runs
    /// and audits denials. If a loader is given and returns null, the platform returns 404.
    /// </summary>
    public static RouteHandlerBuilder RequireAction(this RouteHandlerBuilder builder, string action, ResourceLoader? resource = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(new RequiredActionMetadata(action, resource));
        return builder;
    }
}

/// <summary>Endpoint metadata read by the platform authorization filter.</summary>
public sealed record RequiredActionMetadata(string Action, ResourceLoader? ResourceLoader);
