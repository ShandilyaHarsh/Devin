using System.Text.Json;
using Barebone.Platform.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Barebone.Platform.Internal;

internal sealed class CatalogService(
    IDbContextFactory<PlatformDbContext> factory,
    AppRegistry registry,
    IManifestService manifests,
    TimeProvider time) : ICatalog, ICatalogAdmin
{
    public async Task<IReadOnlyList<CatalogEntry>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.CatalogEntries.AsNoTracking().OrderBy(c => c.Slug).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(Map)];
    }

    public async Task<CatalogEntry?> GetAsync(string slug, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.CatalogEntries.AsNoTracking().FirstOrDefaultAsync(c => c.Slug == slug, cancellationToken).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<CatalogEntry> RegisterAsync(IWriteScope scope, RegisterAppRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var db = DbOf(scope);
        var yaml = manifests.ToYaml(request.Manifest);
        var validation = manifests.Validate(yaml);
        if (!validation.IsValid)
        {
            throw new PlatformRuleViolationException(StatusCodes.Status422UnprocessableEntity, "Invalid manifest", string.Join("; ", validation.Errors));
        }

        var m = request.Manifest;
        if (await db.CatalogEntries.AnyAsync(c => c.Slug == m.Name, cancellationToken).ConfigureAwait(false))
        {
            throw new PlatformRuleViolationException(StatusCodes.Status409Conflict, "App is already registered", "already-registered");
        }

        if (registry.Get(m.Name) is { } code)
        {
            if (code.Manifest.Access != m.Access)
            {
                throw new PlatformRuleViolationException(StatusCodes.Status422UnprocessableEntity, "App code access mode does not match the approved intake", "access-mismatch");
            }

            if (code.Manifest.DataClassification < m.DataClassification)
            {
                throw new PlatformRuleViolationException(StatusCodes.Status422UnprocessableEntity, "App code classification is below the approved intake classification", "classification-too-low");
            }
        }

        var row = new CatalogRow
        {
            Slug = m.Name,
            DisplayName = m.DisplayName,
            Description = m.Description,
            OwnerTeam = m.Owner.Team,
            OwnerEmail = m.Owner.Email,
            BusinessOwnerName = m.BusinessOwner.Name,
            BusinessOwnerEmail = m.BusinessOwner.Email,
            DataClassification = m.DataClassification.ToWire(),
            Access = m.Access.ToWire(),
            Stage = m.Stage.ToWire(),
            AccessListJson = JsonSerializer.Serialize(m.AccessList.Distinct(StringComparer.Ordinal).ToList()),
            RequesterId = request.RequesterId,
            ApprovedBy = request.ApprovedBy,
            RegisteredAt = time.GetUtcNow(),
            ManifestYaml = yaml,
        };
        db.CatalogEntries.Add(row);
        scope.SetResourceId(m.Name);
        return Map(row);
    }

    public async Task<CatalogEntry> SetStageAsync(IWriteScope scope, string slug, AppStage stage, CancellationToken cancellationToken)
    {
        var row = await FindRowAsync(scope, slug, cancellationToken).ConfigureAwait(false);
        row.Stage = stage.ToWire();
        scope.SetResourceId(slug);
        return Map(row);
    }

    public async Task<CatalogEntry> SetAccessListAsync(IWriteScope scope, string slug, IReadOnlyList<string> userIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        var row = await FindRowAsync(scope, slug, cancellationToken).ConfigureAwait(false);
        if (userIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new PlatformRuleViolationException(StatusCodes.Status422UnprocessableEntity, "Access list entries must be user ids", "invalid-access-list");
        }

        _ = EnumNames.TryParseClassification(row.DataClassification, out var classification);
        if (userIds.Contains("*") && classification >= DataClassification.Confidential)
        {
            throw new PlatformRuleViolationException(StatusCodes.Status422UnprocessableEntity, "\"*\" is only allowed for public or internal apps", "wildcard-not-allowed");
        }

        row.AccessListJson = JsonSerializer.Serialize(userIds.Distinct(StringComparer.Ordinal).ToList());
        scope.SetResourceId(slug);
        return Map(row);
    }

    private static async Task<CatalogRow> FindRowAsync(IWriteScope scope, string slug, CancellationToken cancellationToken) =>
        await DbOf(scope).CatalogEntries.FirstOrDefaultAsync(c => c.Slug == slug, cancellationToken).ConfigureAwait(false)
        ?? throw new PlatformRuleViolationException(StatusCodes.Status404NotFound, "App not found", "app-not-registered");

    private static PlatformDbContext DbOf(IWriteScope scope) =>
        (scope as WriteScope)?.Db ?? throw new InvalidOperationException("Catalog mutations must run inside a platform UnitOfWork.");

    private CatalogEntry Map(CatalogRow row)
    {
        _ = EnumNames.TryParseClassification(row.DataClassification, out var classification);
        _ = EnumNames.TryParseAccess(row.Access, out var access);
        _ = EnumNames.TryParseStage(row.Stage, out var stage);
        return new CatalogEntry(
            row.Slug,
            row.DisplayName,
            row.Description,
            new OwnerRef(row.OwnerTeam, row.OwnerEmail),
            new BusinessOwnerRef(row.BusinessOwnerName, row.BusinessOwnerEmail),
            classification,
            access,
            stage,
            JsonSerializer.Deserialize<List<string>>(row.AccessListJson) ?? [],
            row.RequesterId,
            row.ApprovedBy,
            row.RegisteredAt,
            registry.Get(row.Slug)?.Code is not null,
            row.ManifestYaml);
    }
}
