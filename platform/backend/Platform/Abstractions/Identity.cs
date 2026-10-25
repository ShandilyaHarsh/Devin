namespace Barebone.Platform.Abstractions;

public sealed record PlatformUser(string Id, string DisplayName, string Email, IReadOnlyList<string> Roles);

/// <summary>User + effective roles (directory roles plus platform pseudo-roles such as platform:builder).</summary>
public sealed record AuthzSubject(string UserId, IReadOnlySet<string> Roles);

/// <summary>Seam for the identity source. Dev picker now; Entra ID OIDC later.</summary>
public interface IIdentityProvider
{
    string Name { get; }
    Task<IReadOnlyList<PlatformUser>> ListUsersAsync(CancellationToken cancellationToken);
    Task<PlatformUser?> FindUserAsync(string userId, CancellationToken cancellationToken);
}

public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    PlatformUser? User { get; }

    /// <summary>Throws if not authenticated.</summary>
    PlatformUser RequiredUser { get; }

    /// <summary>Effective subject for the current app request (includes pseudo-roles added by the stage gate).</summary>
    AuthzSubject Subject { get; }
    string CorrelationId { get; }
}
