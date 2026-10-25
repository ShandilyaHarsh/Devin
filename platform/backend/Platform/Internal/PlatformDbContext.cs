using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Barebone.Platform.Internal;

internal sealed class AuditEventRow
{
    public long Id { get; set; }
    public string Timestamp { get; set; } = string.Empty;
    public string AppSlug { get; set; } = string.Empty;
    public string ActorId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string? ResourceId { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? Before { get; set; }
    public string? After { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string PrevHash { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
}

internal sealed class IdempotencyRecordRow
{
    public string UserId { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string ResponseBody { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class OutboxRow
{
    public long Id { get; set; }
    public string AppSlug { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}

internal sealed class CatalogRow
{
    public string Slug { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string OwnerTeam { get; set; } = string.Empty;
    public string OwnerEmail { get; set; } = string.Empty;
    public string BusinessOwnerName { get; set; } = string.Empty;
    public string BusinessOwnerEmail { get; set; } = string.Empty;
    public string DataClassification { get; set; } = string.Empty;
    public string Access { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public string AccessListJson { get; set; } = "[]";
    public string? RequesterId { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
    public string ManifestYaml { get; set; } = string.Empty;
}

/// <summary>The single EF Core model: platform tables plus every loaded app's entities.</summary>
internal sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options, AppRegistry registry) : DbContext(options)
{
    public static readonly HashSet<Type> PlatformInternalTypes = [typeof(AuditEventRow), typeof(IdempotencyRecordRow), typeof(OutboxRow)];

    public AppRegistry Registry { get; } = registry;

    public DbSet<AuditEventRow> AuditEvents => Set<AuditEventRow>();
    public DbSet<IdempotencyRecordRow> IdempotencyRecords => Set<IdempotencyRecordRow>();
    public DbSet<OutboxRow> OutboxMessages => Set<OutboxRow>();
    public DbSet<CatalogRow> CatalogEntries => Set<CatalogRow>();

    /// <summary>CLR entity type => owning app slug (populated while building the model).</summary>
    public static Dictionary<Type, string> OwnerOf(IModel model) =>
        model.GetEntityTypes()
            .Where(e => e.FindAnnotation(OwnerAnnotation) is not null)
            .ToDictionary(e => e.ClrType, e => (string)e.FindAnnotation(OwnerAnnotation)!.Value!);

    public const string OwnerAnnotation = "Barebone:AppSlug";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditEventRow>(e =>
        {
            e.ToTable("audit_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.Timestamp).HasColumnName("timestamp");
            e.Property(x => x.AppSlug).HasColumnName("app_slug");
            e.Property(x => x.ActorId).HasColumnName("actor_id");
            e.Property(x => x.Action).HasColumnName("action");
            e.Property(x => x.ResourceType).HasColumnName("resource_type");
            e.Property(x => x.ResourceId).HasColumnName("resource_id");
            e.Property(x => x.Outcome).HasColumnName("outcome");
            e.Property(x => x.Reason).HasColumnName("reason");
            e.Property(x => x.Before).HasColumnName("before");
            e.Property(x => x.After).HasColumnName("after");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.PrevHash).HasColumnName("prev_hash");
            e.Property(x => x.Hash).HasColumnName("hash");
            e.HasIndex(x => x.AppSlug);
        });
        modelBuilder.Entity<IdempotencyRecordRow>(e =>
        {
            e.ToTable("idempotency_records");
            e.HasKey(x => new { x.UserId, x.Method, x.Path, x.Key });
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.Method).HasColumnName("method");
            e.Property(x => x.Path).HasColumnName("path");
            e.Property(x => x.Key).HasColumnName("key");
            e.Property(x => x.RequestHash).HasColumnName("request_hash");
            e.Property(x => x.StatusCode).HasColumnName("status_code");
            e.Property(x => x.ResponseBody).HasColumnName("response_body");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });
        modelBuilder.Entity<OutboxRow>(e =>
        {
            e.ToTable("outbox_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.AppSlug).HasColumnName("app_slug");
            e.Property(x => x.EventType).HasColumnName("event_type");
            e.Property(x => x.PayloadJson).HasColumnName("payload_json");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.SentAt).HasColumnName("sent_at");
            e.HasIndex(x => x.SentAt);
        });
        modelBuilder.Entity<CatalogRow>(e =>
        {
            e.ToTable("catalog_entries");
            e.HasKey(x => x.Slug);
            e.Property(x => x.Slug).HasColumnName("slug");
            e.Property(x => x.DisplayName).HasColumnName("display_name");
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.OwnerTeam).HasColumnName("owner_team");
            e.Property(x => x.OwnerEmail).HasColumnName("owner_email");
            e.Property(x => x.BusinessOwnerName).HasColumnName("business_owner_name");
            e.Property(x => x.BusinessOwnerEmail).HasColumnName("business_owner_email");
            e.Property(x => x.DataClassification).HasColumnName("data_classification");
            e.Property(x => x.Access).HasColumnName("access");
            e.Property(x => x.Stage).HasColumnName("stage");
            e.Property(x => x.AccessListJson).HasColumnName("access_list_json");
            e.Property(x => x.RequesterId).HasColumnName("requester_id");
            e.Property(x => x.ApprovedBy).HasColumnName("approved_by");
            e.Property(x => x.RegisteredAt).HasColumnName("registered_at");
            e.Property(x => x.ManifestYaml).HasColumnName("manifest_yaml");
        });

        foreach (var app in Registry.Apps.Where(a => a.Code is not null))
        {
            var before = modelBuilder.Model.GetEntityTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            app.Code!.ConfigureModel(modelBuilder);
            foreach (var added in modelBuilder.Model.GetEntityTypes().Where(t => !before.Contains(t.Name)).ToList())
            {
                added.SetAnnotation(OwnerAnnotation, app.Manifest.Name);
            }
        }
    }
}

internal sealed class RegistryModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        context is PlatformDbContext p ? (context.GetType(), p.Registry.Signature, designTime) : (context.GetType(), designTime);
}
