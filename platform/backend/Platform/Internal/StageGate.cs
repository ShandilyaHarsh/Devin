using Barebone.Platform.Abstractions;

namespace Barebone.Platform.Internal;

internal sealed record GateResult(bool Allowed, string Reason, CatalogEntry? Entry, AuthzSubject? Subject);

/// <summary>Staged access from the catalog (not the file): builder-only, demo, live.</summary>
internal sealed class StageGate(ICatalog catalog)
{
    public async Task<GateResult> EvaluateAsync(PlatformUser user, string slug, CancellationToken cancellationToken)
    {
        var entry = await catalog.GetAsync(slug, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return new GateResult(false, "app-not-registered", null, null);
        }

        var roles = user.Roles.ToHashSet(StringComparer.Ordinal);
        var isBuilder = roles.Contains("Builder");
        switch (entry.Stage)
        {
            case AppStage.BuilderOnly when isBuilder:
                roles.Add(PseudoRoles.Builder);
                return new GateResult(true, "builder", entry, new AuthzSubject(user.Id, roles));
            case AppStage.BuilderOnly:
                return new GateResult(false, "stage-builder-only", entry, null);
            case AppStage.Demo when isBuilder:
                roles.Add(PseudoRoles.Builder);
                return new GateResult(true, "builder", entry, new AuthzSubject(user.Id, roles));
            case AppStage.Demo when entry.RequesterId is not null && string.Equals(entry.RequesterId, user.Id, StringComparison.Ordinal):
                roles.Add(PseudoRoles.DemoViewer);
                return new GateResult(true, "requester", entry, new AuthzSubject(user.Id, roles));
            case AppStage.Demo:
                return new GateResult(false, "stage-demo", entry, null);
            case AppStage.Live:
                var wildcardOk = entry.AccessList.Contains("*") && entry.DataClassification <= DataClassification.Internal;
                return entry.AccessList.Contains(user.Id) || wildcardOk
                    ? new GateResult(true, "access-list", entry, new AuthzSubject(user.Id, roles))
                    : new GateResult(false, "not-on-access-list", entry, null);
            default:
                return new GateResult(false, "unknown-stage", entry, null);
        }
    }
}
