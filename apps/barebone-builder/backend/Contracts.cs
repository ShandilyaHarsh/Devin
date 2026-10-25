using System.Text.RegularExpressions;
using Barebone.Platform.Abstractions;

namespace Barebone.Builder;

public sealed record IntakeCreate(
    string? Title,
    string? AppSlug,
    string? Problem,
    string? Users,
    string? DataTouched,
    string? DataClassification,
    string? Access,
    double? HoursSavedPerUserPerWeek,
    int? NumberOfUsers);

public sealed record IntakeDto(
    string Id,
    string Title,
    string AppSlug,
    string Problem,
    string Users,
    string DataTouched,
    string DataClassification,
    string Access,
    double HoursSavedPerUserPerWeek,
    int NumberOfUsers,
    string Status,
    string RequesterId,
    string? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? RejectReason,
    Roi Roi,
    string? GeneratedManifestYaml,
    DateTimeOffset CreatedAt)
{
    /// <summary>Set only on the approve response: whether the platform seeded synthetic data for the new app.</summary>
    public bool? SeededSyntheticData { get; init; }

    public static IntakeDto From(Intake i) => new(
        i.Id, i.Title, i.AppSlug, i.Problem, i.Users, i.DataTouched, i.DataClassification, i.Access,
        i.HoursSavedPerUserPerWeek, i.NumberOfUsers, i.Status, i.RequesterId, i.DecidedBy, i.DecidedAt, i.RejectReason,
        RoiModel.Compute(i.HoursSavedPerUserPerWeek, i.NumberOfUsers, i.Access, i.DataClassification),
        i.GeneratedManifestYaml, i.CreatedAt);
}

public sealed record RejectRequest(string? Reason);

public sealed record StageRequest(string? Stage);

public sealed record AccessListRequest(IReadOnlyList<string>? UserIds);

public sealed record DirectoryUser(string Id, string DisplayName, IReadOnlyList<string> Roles);

/// <summary>Wire vocabulary (kebab-case strings, as in platform.yaml and the TS SDK types).</summary>
public static class Vocab
{
    public const string Public = "public";
    public const string Internal = "internal";
    public const string Confidential = "confidential";
    public const string Restricted = "restricted";
    public const string ReadOnly = "read-only";
    public const string Write = "write";
    public const string Everyone = "*";

    public static readonly IReadOnlyDictionary<string, DataClassification> Classifications =
        new Dictionary<string, DataClassification>(StringComparer.Ordinal)
        {
            [Public] = DataClassification.Public,
            [Internal] = DataClassification.Internal,
            [Confidential] = DataClassification.Confidential,
            [Restricted] = DataClassification.Restricted,
        };

    public static readonly IReadOnlyDictionary<string, AccessMode> AccessModes =
        new Dictionary<string, AccessMode>(StringComparer.Ordinal)
        {
            [ReadOnly] = AccessMode.ReadOnly,
            [Write] = AccessMode.Write,
        };

    public static readonly IReadOnlyDictionary<string, AppStage> Stages =
        new Dictionary<string, AppStage>(StringComparer.Ordinal)
        {
            ["builder-only"] = AppStage.BuilderOnly,
            ["demo"] = AppStage.Demo,
            ["live"] = AppStage.Live,
        };

    public static string StageName(AppStage stage) => Stages.First(kv => kv.Value == stage).Key;
}

public static partial class Validation
{
    public const int MaxAccessListSize = 500;

    [GeneratedRegex("^[a-z][a-z0-9-]{2,40}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    public static partial Regex SlugRegex();

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    public static partial Regex IdRegex();

    public static Dictionary<string, string[]> ValidateIntake(IntakeCreate? body)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (body is null)
        {
            errors["body"] = ["Request body is required."];
            return errors;
        }

        Text(errors, "title", body.Title, 3, 120);
        Text(errors, "problem", body.Problem, 10, 2000);
        Text(errors, "users", body.Users, 2, 500);
        Text(errors, "dataTouched", body.DataTouched, 2, 500);
        if (body.AppSlug is null || !SlugRegex().IsMatch(body.AppSlug))
        {
            errors["appSlug"] = ["Must match ^[a-z][a-z0-9-]{2,40}$."];
        }

        ValidateRoiInputs(errors, body.HoursSavedPerUserPerWeek, body.NumberOfUsers, body.Access, body.DataClassification);
        return errors;
    }

    public static void ValidateRoiInputs(
        Dictionary<string, string[]> errors, double? hours, int? users, string? access, string? classification)
    {
        if (hours is not { } h || double.IsNaN(h) || h < 0 || h > 40)
        {
            errors["hoursSavedPerUserPerWeek"] = ["Must be a number between 0 and 40."];
        }

        if (users is not { } u || u < 1 || u > 10000)
        {
            errors["numberOfUsers"] = ["Must be an integer between 1 and 10000."];
        }

        if (access is null || !Vocab.AccessModes.ContainsKey(access))
        {
            errors["access"] = ["Must be one of: read-only, write."];
        }

        if (classification is null || !Vocab.Classifications.ContainsKey(classification))
        {
            errors["dataClassification"] = ["Must be one of: public, internal, confidential, restricted."];
        }
    }

    private static void Text(Dictionary<string, string[]> errors, string field, string? value, int min, int max)
    {
        var length = value?.Trim().Length ?? 0;
        if (length < min || length > max)
        {
            errors[field] = [$"Must be {min}-{max} characters."];
        }
        else if (value!.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
        {
            errors[field] = ["Must not contain control characters."];
        }
    }
}
