using Barebone.Platform.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Kyc.CaseReviewer;

/// <summary>KYC Case Reviewer: entities, endpoints and synthetic seed. All security is delegated to the platform.</summary>
public sealed class KycApp : IPlatformApp
{
    public const string AppSlug = "kyc-case-reviewer";

    public string Slug => AppSlug;

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<KycCase>(b =>
        {
            b.ToTable("kyc_cases");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasMaxLength(64);
            b.Property(x => x.CaseRef).HasMaxLength(32).IsRequired();
            b.HasIndex(x => x.CaseRef).IsUnique();
            b.Property(x => x.CustomerName).HasMaxLength(200).IsRequired();
            b.Property(x => x.SsnLast4).HasMaxLength(4).IsFixedLength().IsRequired();
            b.Property(x => x.Status).HasMaxLength(32).IsRequired();
            b.Property(x => x.AssignedAnalystId).HasMaxLength(64);
            b.Property(x => x.PreparedBy).HasMaxLength(64);
            b.Property(x => x.Recommendation).HasMaxLength(16);
            b.Property(x => x.RecommendationRationale).HasMaxLength(KycEndpoints.MaxTextLength);
            b.Property(x => x.DocumentsJson).HasMaxLength(8000).IsRequired();
            b.ToTable(t => t.HasCheckConstraint("ck_kyc_cases_risk", "RiskScore BETWEEN 0 AND 100"));
        });

        modelBuilder.Entity<ReviewNote>(b =>
        {
            b.ToTable("kyc_review_notes");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasMaxLength(64);
            b.Property(x => x.CaseId).HasMaxLength(64).IsRequired();
            b.HasIndex(x => x.CaseId);
            b.Property(x => x.AuthorId).HasMaxLength(64).IsRequired();
            b.Property(x => x.Text).HasMaxLength(KycEndpoints.MaxTextLength).IsRequired();
        });

        modelBuilder.Entity<KycDecision>(b =>
        {
            b.ToTable("kyc_decisions");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasMaxLength(64);
            b.Property(x => x.CaseId).HasMaxLength(64).IsRequired();
            b.HasIndex(x => x.CaseId).IsUnique();
            b.Property(x => x.DeciderId).HasMaxLength(64).IsRequired();
            b.Property(x => x.Decision).HasMaxLength(16).IsRequired();
            b.Property(x => x.Rationale).HasMaxLength(KycEndpoints.MaxTextLength).IsRequired();
        });
    }

    public void MapEndpoints(RouteGroupBuilder group) => KycEndpoints.Map(group);

    public Task SeedAsync(ISeedContext context, CancellationToken cancellationToken) =>
        KycSeed.SeedAsync(context, cancellationToken);
}
