namespace Barebone.Platform.Abstractions;

public enum DataClassification { Public, Internal, Confidential, Restricted }
public enum AccessMode { ReadOnly, Write }
public enum AppStage { BuilderOnly, Demo, Live }
public enum RegistrationMode { Intake, Bootstrap }

public sealed record OwnerRef(string Team, string Email);
public sealed record BusinessOwnerRef(string Name, string Email);

public sealed record AppManifest
{
    public int SchemaVersion { get; init; } = 1;
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required OwnerRef Owner { get; init; }
    public required BusinessOwnerRef BusinessOwner { get; init; }
    public DataClassification DataClassification { get; init; }
    public AccessMode Access { get; init; }
    public AppStage Stage { get; init; }
    public RegistrationMode Registration { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = [];
    public IReadOnlyList<string> AccessList { get; init; } = [];
    public IReadOnlyList<string> AuditedFields { get; init; } = [];
    public IReadOnlyList<string> PiiFields { get; init; } = [];
    public string CardData { get; init; } = "none";
    public string? BackendAssembly { get; init; }
}

public sealed record CatalogEntry(
    string Slug,
    string DisplayName,
    string Description,
    OwnerRef Owner,
    BusinessOwnerRef BusinessOwner,
    DataClassification DataClassification,
    AccessMode Access,
    AppStage Stage,
    IReadOnlyList<string> AccessList,
    string? RequesterId,
    string? ApprovedBy,
    DateTimeOffset RegisteredAt,
    bool HasCode,
    string ManifestYaml);

public sealed record RegisterAppRequest(AppManifest Manifest, string RequesterId, string ApprovedBy);

public interface ICatalog
{
    Task<IReadOnlyList<CatalogEntry>> ListAsync(CancellationToken cancellationToken);
    Task<CatalogEntry?> GetAsync(string slug, CancellationToken cancellationToken);
}

/// <summary>Catalog mutations. Must be called inside a UnitOfWork (pass its scope) so they are atomic and audited.</summary>
public interface ICatalogAdmin
{
    Task<CatalogEntry> RegisterAsync(IWriteScope scope, RegisterAppRequest request, CancellationToken cancellationToken);
    Task<CatalogEntry> SetStageAsync(IWriteScope scope, string slug, AppStage stage, CancellationToken cancellationToken);
    Task<CatalogEntry> SetAccessListAsync(IWriteScope scope, string slug, IReadOnlyList<string> userIds, CancellationToken cancellationToken);
}

/// <summary>Parses/validates/serialises platform.yaml. Validate never throws; Parse throws ManifestValidationException.</summary>
public interface IManifestService
{
    ManifestValidationResult Validate(string yaml);
    AppManifest Parse(string yaml);
    string ToYaml(AppManifest manifest);
}

public sealed record ManifestValidationResult(bool IsValid, IReadOnlyList<string> Errors);

public sealed class ManifestValidationException(IReadOnlyList<string> errors)
    : Exception("Invalid platform manifest: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
