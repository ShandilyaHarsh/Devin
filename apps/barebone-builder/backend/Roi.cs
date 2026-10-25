namespace Barebone.Builder;

public sealed record Roi(
    double AnnualHoursSaved,
    double AnnualValueUsd,
    int BuildHours,
    double BuildCostUsd,
    double AnnualRunCostUsd,
    double? PaybackMonths,
    string Recommendation);

/// <summary>All ROI constants and the formula from docs/prototype-plan.md section 4.</summary>
public static class RoiModel
{
    public const int WorkingWeeksPerYear = 46;
    public const double ValuePerHourUsd = 85;
    public const int BuildHoursWrite = 40;
    public const int BuildHoursReadOnly = 16;
    public const int ExtraBuildHoursRestricted = 24;
    public const int ExtraBuildHoursConfidential = 12;
    public const double BuildCostPerHourUsd = 150;
    public const double BaseAnnualRunCostUsd = 2400;
    public const double ExtraRunCostRestrictedUsd = 3600;
    public const double ExtraRunCostConfidentialUsd = 1200;
    public const double BuildPaybackThresholdMonths = 12;

    public static Roi Compute(double hoursSavedPerUserPerWeek, int numberOfUsers, string access, string dataClassification)
    {
        var annualHoursSaved = hoursSavedPerUserPerWeek * numberOfUsers * WorkingWeeksPerYear;
        var annualValueUsd = annualHoursSaved * ValuePerHourUsd;
        var buildHours = (access == Vocab.Write ? BuildHoursWrite : BuildHoursReadOnly)
            + dataClassification switch
            {
                Vocab.Restricted => ExtraBuildHoursRestricted,
                Vocab.Confidential => ExtraBuildHoursConfidential,
                _ => 0,
            };
        var buildCostUsd = buildHours * BuildCostPerHourUsd;
        var annualRunCostUsd = BaseAnnualRunCostUsd + dataClassification switch
        {
            Vocab.Restricted => ExtraRunCostRestrictedUsd,
            Vocab.Confidential => ExtraRunCostConfidentialUsd,
            _ => 0,
        };
        var monthlyNet = (annualValueUsd - annualRunCostUsd) / 12;
        double? paybackMonths = monthlyNet > 0
            ? Math.Round(buildCostUsd / monthlyNet, 1, MidpointRounding.AwayFromZero)
            : null;
        var recommendation = paybackMonths is { } p && p <= BuildPaybackThresholdMonths ? "build" : "review";
        return new Roi(annualHoursSaved, annualValueUsd, buildHours, buildCostUsd, annualRunCostUsd, paybackMonths, recommendation);
    }
}
