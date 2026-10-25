namespace Barebone.Builder;

/// <summary>A request for a new internal tool. Persisted in table builder_intakes.</summary>
public sealed class Intake
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string AppSlug { get; set; } = string.Empty;
    public string Problem { get; set; } = string.Empty;
    public string Users { get; set; } = string.Empty;
    public string DataTouched { get; set; } = string.Empty;
    public string DataClassification { get; set; } = string.Empty;
    public string Access { get; set; } = string.Empty;
    public double HoursSavedPerUserPerWeek { get; set; }
    public int NumberOfUsers { get; set; }
    public string Status { get; set; } = IntakeStatus.Submitted;
    public string RequesterId { get; set; } = string.Empty;
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? RejectReason { get; set; }
    public string? GeneratedManifestYaml { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class IntakeStatus
{
    public const string Submitted = "submitted";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
}
