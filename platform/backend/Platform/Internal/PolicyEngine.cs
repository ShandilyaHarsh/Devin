using System.Collections;
using System.Globalization;
using Barebone.Platform.Abstractions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Barebone.Platform.Internal;

internal sealed record PolicyCondition(string Attr, string Op, object? Value);

internal sealed record PolicyRule(
    int Index,
    string Action,
    bool IsDeny,
    IReadOnlyList<string> Roles,
    IReadOnlyList<PolicyCondition> AllOf,
    IReadOnlyList<PolicyCondition> AnyOf,
    string? Reason)
{
    public string Name => (IsDeny ? "deny:" : "allow:") + Action + "#" + Index.ToString(CultureInfo.InvariantCulture);
}

internal sealed record PolicyDocument(string App, IReadOnlyList<PolicyRule> Rules)
{
    public IEnumerable<string> ReferencedRoles => Rules.SelectMany(r => r.Roles).Distinct(StringComparer.Ordinal);
}

internal static class PolicyParser
{
    private static readonly HashSet<string> Ops = new(StringComparer.Ordinal) { "eq", "neq", "in", "notIn", "isNull", "notNull" };

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    /// <summary>Parses and validates a policy file. Throws <see cref="InvalidOperationException"/> on any problem (fail closed).</summary>
    public static PolicyDocument Parse(string yaml, string expectedApp, IReadOnlyCollection<string>? allowedRoles)
    {
        var errors = new List<string>();
        PolicyDto? dto;
        try
        {
            dto = Deserializer.Deserialize<PolicyDto>(yaml);
        }
        catch (YamlException ex)
        {
            throw new InvalidOperationException($"Invalid policy for '{expectedApp}': {ex.InnerException?.Message ?? ex.Message}", ex);
        }

        if (dto is null)
        {
            throw new InvalidOperationException($"Invalid policy for '{expectedApp}': empty");
        }

        if (dto.App != expectedApp)
        {
            errors.Add($"app must equal '{expectedApp}'");
        }

        var rules = new List<PolicyRule>();
        var i = 0;
        foreach (var r in dto.Rules ?? [])
        {
            var where = $"rules[{i}]";
            if (string.IsNullOrWhiteSpace(r.Action))
            {
                errors.Add(where + ".action is required");
            }

            if (r.Effect is not ("allow" or "deny"))
            {
                errors.Add(where + ".effect must be allow|deny");
            }

            var roles = r.Roles ?? [];
            if (roles.Count == 0 || roles.Any(string.IsNullOrWhiteSpace))
            {
                errors.Add(where + ".roles must be a non-empty list");
            }

            foreach (var role in roles)
            {
                if (allowedRoles is not null && !IsBuiltInRole(role) && !allowedRoles.Contains(role))
                {
                    errors.Add($"{where} references role '{role}' not declared in the manifest roles");
                }
            }

            var allOf = Conditions(r.When?.AllOf, where + ".when.allOf", errors);
            var anyOf = Conditions(r.When?.AnyOf, where + ".when.anyOf", errors);
            rules.Add(new PolicyRule(i, r.Action ?? string.Empty, r.Effect == "deny", roles, allOf, anyOf, r.Reason));
            i++;
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Invalid policy for '{expectedApp}': " + string.Join("; ", errors));
        }

        return new PolicyDocument(expectedApp, rules);
    }

    private static bool IsBuiltInRole(string role) =>
        role is "*" or "Builder" or "Auditor" or PseudoRoles.Builder or PseudoRoles.DemoViewer;

    private static List<PolicyCondition> Conditions(List<ConditionDto>? list, string where, List<string> errors)
    {
        var result = new List<PolicyCondition>();
        foreach (var c in list ?? [])
        {
            if (string.IsNullOrWhiteSpace(c.Attr) || !(c.Attr.StartsWith("resource.", StringComparison.Ordinal) || c.Attr == "user.id"))
            {
                errors.Add(where + ".attr must be resource.<name> or user.id");
            }

            if (c.Op is null || !Ops.Contains(c.Op))
            {
                errors.Add(where + ".op must be one of " + string.Join("|", Ops));
            }
            else if (c.Op is "in" or "notIn" && c.Value is not IList)
            {
                errors.Add(where + $".value must be a list for {c.Op}");
            }
            else if (c.Op is "eq" or "neq" && (c.Value is null || c.Value is IList))
            {
                errors.Add(where + $".value must be a scalar for {c.Op}");
            }

            result.Add(new PolicyCondition(c.Attr ?? string.Empty, c.Op ?? string.Empty, c.Value));
        }

        return result;
    }

    internal sealed class PolicyDto
    {
        public string? App { get; set; }
        public List<RuleDto>? Rules { get; set; }
    }

    internal sealed class RuleDto
    {
        public string? Action { get; set; }
        public string? Effect { get; set; }
        public List<string>? Roles { get; set; }
        public WhenDto? When { get; set; }
        public string? Reason { get; set; }
    }

    internal sealed class WhenDto
    {
        public List<ConditionDto>? AllOf { get; set; }
        public List<ConditionDto>? AnyOf { get; set; }
    }

    internal sealed class ConditionDto
    {
        public string? Attr { get; set; }
        public string? Op { get; set; }
        public object? Value { get; set; }
    }
}

/// <summary>Deny-by-default evaluator. Any matching deny wins; else any matching allow; else deny (no-matching-rule).</summary>
internal sealed class Authorizer(AppRegistry registry) : IAuthorizer
{
    public AuthzDecision Authorize(AuthzSubject subject, string appSlug, string action, ResourceRef? resource)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var policy = registry.GetPolicy(appSlug);
        if (policy is null || string.IsNullOrEmpty(subject.UserId))
        {
            return new AuthzDecision(false, "no-policy", null);
        }

        PolicyRule? allow = null;
        foreach (var rule in policy.Rules)
        {
            if (!string.Equals(rule.Action, action, StringComparison.Ordinal) || !RoleMatches(rule, subject))
            {
                continue;
            }

            if (rule.IsDeny)
            {
                // Fail closed: a deny whose conditions cannot be evaluated (no resource) applies.
                if (resource is null && HasResourceConditions(rule) || ConditionsHold(rule, subject, resource))
                {
                    return new AuthzDecision(false, rule.Reason ?? "denied-by-policy", rule.Name);
                }
            }
            else if (allow is null && ConditionsHold(rule, subject, resource))
            {
                allow = rule;
            }
        }

        return allow is null
            ? new AuthzDecision(false, "no-matching-rule", null)
            : new AuthzDecision(true, allow.Reason ?? "allowed", allow.Name);
    }

    private static bool RoleMatches(PolicyRule rule, AuthzSubject subject) =>
        rule.Roles.Any(r => r == "*" || subject.Roles.Contains(r));

    private static bool HasResourceConditions(PolicyRule rule) =>
        rule.AllOf.Concat(rule.AnyOf).Any(c => c.Attr.StartsWith("resource.", StringComparison.Ordinal));

    private static bool ConditionsHold(PolicyRule rule, AuthzSubject subject, ResourceRef? resource)
    {
        if (resource is null && HasResourceConditions(rule))
        {
            return false;
        }

        return rule.AllOf.All(c => Evaluate(c, subject, resource))
            && (rule.AnyOf.Count == 0 || rule.AnyOf.Any(c => Evaluate(c, subject, resource)));
    }

    private static bool Evaluate(PolicyCondition c, AuthzSubject subject, ResourceRef? resource)
    {
        var actual = Normalize(ResolveAttr(c.Attr, subject, resource));
        return c.Op switch
        {
            "isNull" => actual is null,
            "notNull" => actual is not null,
            "eq" => actual is not null && actual == Normalize(ResolveValue(c.Value, subject)),
            "neq" => actual != Normalize(ResolveValue(c.Value, subject)),
            "in" => actual is not null && ListValues(c.Value, subject).Contains(actual),
            "notIn" => !ListValues(c.Value, subject).Contains(actual),
            _ => false,
        };
    }

    private static object? ResolveAttr(string attr, AuthzSubject subject, ResourceRef? resource)
    {
        if (attr == "user.id")
        {
            return subject.UserId;
        }

        var name = attr["resource.".Length..];
        if (resource is null)
        {
            return null;
        }

        if (name == "id")
        {
            return resource.Id;
        }

        if (name == "type")
        {
            return resource.Type;
        }

        return resource.Attributes.TryGetValue(name, out var v) ? v : null;
    }

    private static object? ResolveValue(object? value, AuthzSubject subject) =>
        value is "$user.id" ? subject.UserId : value;

    private static HashSet<string?> ListValues(object? value, AuthzSubject subject) =>
        value is IList list
            ? [.. list.Cast<object?>().Select(v => Normalize(ResolveValue(v, subject)))]
            : [];

    private static string? Normalize(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        Enum e => e.ToString(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
