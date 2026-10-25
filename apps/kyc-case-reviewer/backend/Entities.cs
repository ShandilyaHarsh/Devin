using Barebone.Platform.Abstractions;

namespace Kyc.CaseReviewer;

/// <summary>A KYC case. Synthetic data only. Only the last 4 SSN digits are ever stored; no card data.</summary>
public sealed class KycCase
{
    public string Id { get; set; } = string.Empty;

    public string CaseRef { get; set; } = string.Empty;

    [Pii]
    [Synthetic(SyntheticKind.FullName)]
    public string CustomerName { get; set; } = string.Empty;

    [Pii]
    [Synthetic(SyntheticKind.Last4Digits)]
    public string SsnLast4 { get; set; } = string.Empty;

    [Pii]
    [Synthetic(SyntheticKind.DateOfBirth)]
    public DateOnly? DateOfBirth { get; set; }

    [Synthetic(SyntheticKind.RiskScore)]
    public int RiskScore { get; set; }

    public string Status { get; set; } = KycStatus.New;

    public string? AssignedAnalystId { get; set; }

    public string? PreparedBy { get; set; }

    public string? Recommendation { get; set; }

    public string? RecommendationRationale { get; set; }

    /// <summary>JSON array of <see cref="DocumentMeta"/>. Metadata only, never file contents.</summary>
    public string DocumentsJson { get; set; } = "[]";
}

public sealed class ReviewNote
{
    public string Id { get; set; } = string.Empty;

    public string CaseId { get; set; } = string.Empty;

    public string AuthorId { get; set; } = string.Empty;

    /// <summary>Free text may contain sensitive details, so it is masked in audit diffs.</summary>
    [Pii]
    public string Text { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A senior reviewer's final decision on a case (the "checker" step).</summary>
public sealed class KycDecision
{
    public string Id { get; set; } = string.Empty;

    public string CaseId { get; set; } = string.Empty;

    public string DeciderId { get; set; } = string.Empty;

    public string Decision { get; set; } = string.Empty;

    public string Rationale { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record DocumentMeta(string Name, string Type, DateTimeOffset UploadedAt);

public static class KycStatus
{
    public const string New = "new";
    public const string InReview = "in-review";
    public const string PendingSeniorReview = "pending-senior-review";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
}
