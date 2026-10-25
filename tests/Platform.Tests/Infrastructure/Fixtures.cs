using System.Collections.Concurrent;
using System.Text.Json;
using Barebone.Platform.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Platform.Tests.Infrastructure;

public sealed class CapturingPublisher : IOutboxPublisher
{
    public ConcurrentQueue<OutboxMessage> Messages { get; } = new();

    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        Messages.Enqueue(message);

        // Widen the race window so concurrent relays would double-publish if the claim were not conditional.
        await Task.Delay(50, cancellationToken);
    }

    public int CountOf(string eventType) => Messages.Count(m => m.EventType == eventType);
}

/// <summary>Host whose IOutboxPublisher is replaced by a capturing fake.</summary>
public sealed class CapturingFactory : PlatformFactory
{
    public CapturingFactory()
    {
        WithServices(services =>
        {
            services.RemoveAll<IOutboxPublisher>();
            services.AddSingleton<IOutboxPublisher>(Publisher);
        });
    }

    public CapturingPublisher Publisher { get; } = new();
}

/// <summary>KYC registered via the Builder and promoted to live with all KYC users + the auditor on the access list.</summary>
public sealed class KycLiveFactory : PlatformFactory, IAsyncLifetime
{
    private readonly HashSet<string> _taken = new(StringComparer.Ordinal);

    public async Task InitializeAsync() =>
        await Scenario.PromoteKycAsync(this, "live", Users.Carol, Users.Dan, Users.Erin, Users.Grace, Users.Frank);

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    /// <summary>Any seeded case id, for tests that only perform reads or expect denials (no state change).</summary>
    public async Task<string> AnyCaseAsync()
    {
        using var erin = await LoginAsAsync(Users.Erin);
        var cases = await Scenario.ListCasesAsync(erin);
        Assert.NotEmpty(cases);
        return cases[0].Id();
    }

    /// <summary>A seeded case that is unassigned and new, not handed out to another test in this class.</summary>
    public async Task<string> TakeUnassignedCaseAsync()
    {
        using var erin = await LoginAsAsync(Users.Erin);
        var cases = await Scenario.ListCasesAsync(erin);
        foreach (var c in cases)
        {
            var id = c.Id();
            if (c.Str("assignedAnalystId") is null && c.Str("status") == "new" && _taken.Add(id))
            {
                return id;
            }
        }

        Assert.Fail($"Not enough seeded unassigned 'new' KYC cases (got {cases.Count}).");
        return string.Empty;
    }
}

public static class Manifests
{
    /// <summary>The example manifest from docs/prototype-plan.md 3.7 (valid).</summary>
    public const string ValidKyc = """
        schemaVersion: 1
        name: kyc-case-reviewer
        displayName: KYC Case Reviewer
        description: Review and decide KYC cases with maker-checker.
        owner: { team: kyc-engineering, email: kyc-eng@example.test }
        businessOwner: { name: Head of Compliance Ops, email: compliance-ops@example.test }
        dataClassification: restricted
        access: write
        stage: builder-only
        registration: intake
        roles: [KYC Analyst, KYC Senior Reviewer, Auditor]
        accessList: []
        auditedFields: [status, recommendation, decision, assignedAnalystId]
        piiFields: [customerName, dateOfBirth, ssnLast4]
        cardData: none
        backend: { assembly: Kyc.CaseReviewer }
        """;

    public static string Replace(string yaml, string linePrefix, string? newLine)
    {
        var lines = yaml.Split('\n').ToList();
        var index = lines.FindIndex(l => l.StartsWith(linePrefix, StringComparison.Ordinal));
        Assert.True(index >= 0, "line not found: " + linePrefix);
        if (newLine is null)
        {
            lines.RemoveAt(index);
        }
        else
        {
            lines[index] = newLine;
        }

        return string.Join('\n', lines);
    }

    public static AppManifest ProbeManifest(string slug) => new()
    {
        Name = slug,
        DisplayName = "Probe " + slug,
        Description = "Test probe app without code.",
        Owner = new OwnerRef("platform-tests", "tests@example.test"),
        BusinessOwner = new BusinessOwnerRef("Test Owner", "owner@example.test"),
        DataClassification = DataClassification.Internal,
        Access = AccessMode.ReadOnly,
        Stage = AppStage.BuilderOnly,
        Registration = RegistrationMode.Intake,
        Roles = ["Requester"],
        CardData = "none",
    };
}

public static class JsonText
{
    /// <summary>Every string value anywhere in the document.</summary>
    public static IEnumerable<(string Path, string Value)> Strings(JsonElement element, string path = "$")
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                yield return (path, element.GetString()!);
                break;
            case JsonValueKind.Object:
                foreach (var p in element.EnumerateObject())
                {
                    foreach (var s in Strings(p.Value, path + "." + p.Name))
                    {
                        yield return s;
                    }
                }

                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var s in Strings(item, $"{path}[{i++}]"))
                    {
                        yield return s;
                    }
                }

                break;
            default:
                break;
        }
    }
}
