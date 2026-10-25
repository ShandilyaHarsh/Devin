using Barebone.Platform.Abstractions;

namespace Barebone.Platform.Internal;

internal static class EnumNames
{
    public static string ToWire(this DataClassification value) => value switch
    {
        DataClassification.Public => "public",
        DataClassification.Internal => "internal",
        DataClassification.Confidential => "confidential",
        _ => "restricted",
    };

    public static string ToWire(this AccessMode value) => value == AccessMode.Write ? "write" : "read-only";

    public static string ToWire(this AppStage value) => value switch
    {
        AppStage.Live => "live",
        AppStage.Demo => "demo",
        _ => "builder-only",
    };

    public static string ToWire(this RegistrationMode value) => value == RegistrationMode.Bootstrap ? "bootstrap" : "intake";

    public static bool TryParseClassification(string? value, out DataClassification result)
    {
        (var ok, result) = value switch
        {
            "public" => (true, DataClassification.Public),
            "internal" => (true, DataClassification.Internal),
            "confidential" => (true, DataClassification.Confidential),
            "restricted" => (true, DataClassification.Restricted),
            _ => (false, DataClassification.Restricted),
        };
        return ok;
    }

    public static bool TryParseAccess(string? value, out AccessMode result)
    {
        (var ok, result) = value switch
        {
            "read-only" => (true, AccessMode.ReadOnly),
            "write" => (true, AccessMode.Write),
            _ => (false, AccessMode.ReadOnly),
        };
        return ok;
    }

    public static bool TryParseStage(string? value, out AppStage result)
    {
        (var ok, result) = value switch
        {
            "builder-only" => (true, AppStage.BuilderOnly),
            "demo" => (true, AppStage.Demo),
            "live" => (true, AppStage.Live),
            _ => (false, AppStage.BuilderOnly),
        };
        return ok;
    }

    public static bool TryParseRegistration(string? value, out RegistrationMode result)
    {
        (var ok, result) = value switch
        {
            "intake" => (true, RegistrationMode.Intake),
            "bootstrap" => (true, RegistrationMode.Bootstrap),
            _ => (false, RegistrationMode.Intake),
        };
        return ok;
    }
}
