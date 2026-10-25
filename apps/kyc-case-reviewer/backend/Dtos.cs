namespace Kyc.CaseReviewer;

public sealed record CaseSummary(
    string Id,
    string CaseRef,
    string CustomerName,
    int RiskScore,
    string Status,
    string? AssignedAnalystId,
    string? PreparedBy,
    bool CanAssign);

public sealed record PermissionHint(bool Allowed, string Reason);

public sealed record CasePermissions(
    PermissionHint Reveal,
    PermissionHint Assign,
    PermissionHint AddNote,
    PermissionHint Recommend,
    PermissionHint Decide);

public sealed record NoteDto(string Id, string AuthorId, string Text, DateTimeOffset CreatedAt);

public sealed record DecisionDto(string Id, string DeciderId, string Decision, string Rationale, DateTimeOffset CreatedAt);

/// <summary>Case detail with PII always masked.</summary>
public sealed record CaseDetail(
    string Id,
    string CaseRef,
    string CustomerName,
    string DateOfBirth,
    string SsnLast4,
    int RiskScore,
    string Status,
    string? AssignedAnalystId,
    string? PreparedBy,
    string? Recommendation,
    string? RecommendationRationale,
    IReadOnlyList<DocumentMeta> Documents,
    IReadOnlyList<NoteDto> Notes,
    IReadOnlyList<DecisionDto> Decisions,
    CasePermissions? Permissions);

/// <summary>Unmasked PII, returned only by the audited reveal endpoint.</summary>
public sealed record RevealedPii(string CustomerName, string? DateOfBirth, string SsnLast4);

public sealed record NoteRequest(string? Text);

public sealed record RecommendationRequest(string? Recommendation, string? Rationale);

public sealed record DecisionRequest(string? Decision, string? Rationale);
