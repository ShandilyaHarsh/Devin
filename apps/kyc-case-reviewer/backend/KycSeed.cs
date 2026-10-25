using System.Globalization;
using Barebone.Platform.Abstractions;

namespace Kyc.CaseReviewer;

/// <summary>Deterministic synthetic seed: 12 cases with a mix of statuses and assignments.</summary>
internal static class KycSeed
{
    private sealed record Plan(string Status, string? Assigned, string? PreparedBy, string? Recommendation, string? DeciderId);

    private static readonly Plan[] Plans =
    [
        new(KycStatus.New, null, null, null, null),
        new(KycStatus.New, null, null, null, null),
        new(KycStatus.New, null, null, null, null),
        new(KycStatus.InReview, "u-carol", null, null, null),
        new(KycStatus.InReview, "u-carol", null, null, null),
        new(KycStatus.InReview, "u-dan", null, null, null),
        new(KycStatus.InReview, "u-dan", null, null, null),
        new(KycStatus.PendingSeniorReview, "u-carol", "u-carol", "approve", null),
        new(KycStatus.PendingSeniorReview, "u-grace", "u-grace", "reject", null),
        new(KycStatus.PendingSeniorReview, "u-dan", "u-dan", "escalate", null),
        new(KycStatus.Approved, "u-carol", "u-carol", "approve", "u-erin"),
        new(KycStatus.Rejected, "u-dan", "u-dan", "reject", "u-erin"),
    ];

    private static readonly string[] DocumentTypes = ["passport", "drivers-license", "utility-bill", "bank-statement"];

    public static Task SeedAsync(ISeedContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var generator = context.Generator;
        var random = generator.Random;
        var scope = context.Scope;
        var baseTime = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
        var generated = generator.Generate<KycCase>(Plans.Length);

        for (var i = 0; i < Plans.Length; i++)
        {
            var plan = Plans[i];
            var n = i + 1;
            var c = i < generated.Count ? generated[i] : new KycCase();
            c.Id = Inv($"case-{n:D3}");
            c.CaseRef = Inv($"KYC-2026-{n:D4}");
            if (string.IsNullOrWhiteSpace(c.CustomerName))
            {
                c.CustomerName = Inv($"Synthetic Customer{n}");
            }

            // Restricted app: only obviously fake last-4 values from the 9xxx range.
            if (c.SsnLast4 is not { Length: 4 } || c.SsnLast4[0] != '9' || !c.SsnLast4.All(char.IsAsciiDigit))
            {
                c.SsnLast4 = Inv($"9{random.Next(0, 1000):D3}");
            }

            c.DateOfBirth ??= new DateOnly(1960, 1, 1).AddDays(random.Next(0, 365 * 40));
            c.RiskScore = Math.Clamp(c.RiskScore is > 0 and <= 100 ? c.RiskScore : random.Next(5, 96), 0, 100);
            c.Status = plan.Status;
            c.AssignedAnalystId = plan.Assigned;
            c.PreparedBy = plan.PreparedBy;
            c.Recommendation = plan.Recommendation;
            c.RecommendationRationale = plan.Recommendation is null ? null : "Synthetic rationale: documents checked against synthetic watchlist.";

            var docCount = 1 + random.Next(0, 3);
            var documents = Enumerable.Range(0, docCount)
                .Select(d =>
                {
                    var type = DocumentTypes[(i + d) % DocumentTypes.Length];
                    return new DocumentMeta(Inv($"{type}-{n:D3}.pdf"), type, baseTime.AddDays(n).AddHours(d));
                })
                .ToList();
            c.DocumentsJson = KycEndpoints.SerializeDocuments(documents);
            scope.Add(c);

            if (plan.Assigned is not null)
            {
                scope.Add(new ReviewNote
                {
                    Id = Inv($"note-{n:D3}-1"),
                    CaseId = c.Id,
                    AuthorId = plan.Assigned,
                    Text = "Synthetic note: initial document review started.",
                    CreatedAt = baseTime.AddDays(n).AddHours(4),
                });
            }

            if (plan.DeciderId is not null)
            {
                scope.Add(new KycDecision
                {
                    Id = Inv($"dec-{n:D3}"),
                    CaseId = c.Id,
                    DeciderId = plan.DeciderId,
                    Decision = plan.Recommendation == "approve" ? "approve" : "reject",
                    Rationale = "Synthetic decision: concur with analyst recommendation.",
                    CreatedAt = baseTime.AddDays(n).AddHours(8),
                });
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static string Inv(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
