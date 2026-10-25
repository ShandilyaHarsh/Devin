using System.Net;
using Barebone.Platform.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Platform.Tests.Infrastructure;

namespace Platform.Tests;

public sealed class AuthorizationTests(KycLiveFactory factory) : IClassFixture<KycLiveFactory>
{
    [Fact]
    public async Task UserWithNoRolesIsDeniedEverywhereAndEachDenialIsAudited()
    {
        var deniedBefore = (await Scenario.DeniedAuditForAsync(factory, Users.Mallory)).Count;
        using var mallory = await factory.LoginAsAsync(Users.Mallory);

        await Scenario.AssertStatusAsync(HttpStatusCode.Forbidden, mallory.GetAsync(Paths.Builder + "/intakes"), "mallory list intakes");
        await Scenario.AssertStatusAsync(HttpStatusCode.Forbidden, mallory.PostAsync(Paths.Builder + "/intakes", Scenario.IntakeBody("mallory-app")), "mallory create intake");
        await Scenario.AssertStatusAsync(HttpStatusCode.Forbidden, mallory.GetAsync(Paths.Kyc + "/cases"), "mallory list cases");

        var deniedAfter = await Scenario.DeniedAuditForAsync(factory, Users.Mallory);
        Assert.Equal(deniedBefore + 3, deniedAfter.Count);
        Assert.All(deniedAfter, r => Assert.False(string.IsNullOrWhiteSpace(r.Str("reason"))));
    }

    [Fact]
    public void ActionWithNoMatchingRuleIsDenied()
    {
        var authorizer = factory.Services.GetRequiredService<IAuthorizer>();
        var builder = new AuthzSubject(Users.Bob, new HashSet<string>(StringComparer.Ordinal) { "Builder", PseudoRoles.Builder });

        var unknown = authorizer.Authorize(builder, "barebone-builder", "definitely.not.a.declared.action", null);
        Assert.False(unknown.Allowed);
        Assert.Contains("no-matching-rule", unknown.Reason, StringComparison.Ordinal);

        var unknownApp = authorizer.Authorize(builder, "no-such-app", "intake.list", null);
        Assert.False(unknownApp.Allowed);

        var noRoles = authorizer.Authorize(new AuthzSubject(Users.Mallory, new HashSet<string>()), "barebone-builder", "intake.list", null);
        Assert.False(noRoles.Allowed);

        // Positive control so the denials above are meaningful.
        var allowed = authorizer.Authorize(builder, "barebone-builder", "intake.list", null);
        Assert.True(allowed.Allowed, allowed.Reason);
    }

    [Fact]
    public async Task RequesterCannotApproveIntakes()
    {
        using var alice = await factory.LoginAsAsync(Users.Alice);
        var intake = await Scenario.SubmitIntakeAsync(alice, "alice-self-" + Guid.NewGuid().ToString("N")[..6], "internal", "read-only");
        await Scenario.AssertStatusAsync(HttpStatusCode.Forbidden, alice.PostAsync($"{Paths.Builder}/intakes/{intake.Id()}/approve", new { }), "requester approves");
    }

    [Fact]
    public async Task BuilderMakerCheckerBlocksSelfApproval()
    {
        using var bob = await factory.LoginAsAsync(Users.Bob);
        var intake = await Scenario.SubmitIntakeAsync(bob, "bob-tool-" + Guid.NewGuid().ToString("N")[..6], "internal", "read-only");
        Assert.Equal(Users.Bob, intake.Str("requesterId"));
        var deniedBefore = (await Scenario.DeniedAuditForAsync(factory, Users.Bob)).Count;

        await Scenario.AssertStatusAsync(HttpStatusCode.Forbidden, bob.PostAsync($"{Paths.Builder}/intakes/{intake.Id()}/approve", new { }), "bob approves own intake");
        await Scenario.AssertStatusAsync(HttpStatusCode.Forbidden, bob.PostAsync($"{Paths.Builder}/intakes/{intake.Id()}/reject", new { reason = "self" }), "bob rejects own intake");
        Assert.Equal(deniedBefore + 2, (await Scenario.DeniedAuditForAsync(factory, Users.Bob)).Count);
        Assert.Equal("submitted", (await bob.GetJsonAsync($"{Paths.Builder}/intakes/{intake.Id()}")).Str("status"));

        using var bianca = await factory.LoginAsAsync(Users.Bianca);
        await bianca.PostJsonAsync($"{Paths.Builder}/intakes/{intake.Id()}/approve", new { });
        var decided = await bianca.GetJsonAsync($"{Paths.Builder}/intakes/{intake.Id()}");
        Assert.Equal("approved", decided.Str("status"));
        Assert.Equal(Users.Bianca, decided.Str("decidedBy"));
    }

    [Fact]
    public async Task KycMakerCheckerBlocksPreparerFromDeciding()
    {
        var caseId = await factory.TakeUnassignedCaseAsync();
        using var grace = await factory.LoginAsAsync(Users.Grace);
        await grace.PostJsonAsync($"{Paths.Kyc}/cases/{caseId}/assign", new { });
        await grace.PostJsonAsync($"{Paths.Kyc}/cases/{caseId}/recommendation", new { recommendation = "approve", rationale = "Documents consistent." });
        Assert.Equal("pending-senior-review", (await grace.GetJsonAsync($"{Paths.Kyc}/cases/{caseId}")).Str("status"));

        using (var selfDecision = await grace.PostAsync($"{Paths.Kyc}/cases/{caseId}/decision", new { decision = "approve", rationale = "Self check." }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, selfDecision.StatusCode);
        }

        var graceDenials = await Scenario.DeniedAuditForAsync(factory, Users.Grace);
        Assert.Contains(graceDenials, r => r.Str("resourceId") == caseId && (r.Str("reason") ?? string.Empty).Contains("maker-checker", StringComparison.Ordinal));

        using var erin = await factory.LoginAsAsync(Users.Erin);
        await erin.PostJsonAsync($"{Paths.Kyc}/cases/{caseId}/decision", new { decision = "approve", rationale = "Independent review." });
        Assert.Equal("approved", (await erin.GetJsonAsync($"{Paths.Kyc}/cases/{caseId}")).Str("status"));
    }

    [Fact]
    public async Task DecisionRequiresPendingSeniorReview()
    {
        var caseId = await factory.TakeUnassignedCaseAsync();
        using var erin = await factory.LoginAsAsync(Users.Erin);
        using var response = await erin.PostAsync($"{Paths.Kyc}/cases/{caseId}/decision", new { decision = "approve", rationale = "Skipping review." });
        Assert.False(response.IsSuccessStatusCode, "deciding a 'new' case must fail");
        Assert.Equal("new", (await erin.GetJsonAsync($"{Paths.Kyc}/cases/{caseId}")).Str("status"));
    }

    [Fact]
    public async Task AnalystCannotSeeCaseAssignedToAnotherAnalyst()
    {
        var caseId = await factory.TakeUnassignedCaseAsync();
        using var carol = await factory.LoginAsAsync(Users.Carol);
        await carol.PostJsonAsync($"{Paths.Kyc}/cases/{caseId}/assign", new { });
        Assert.Equal(Users.Carol, (await carol.GetJsonAsync($"{Paths.Kyc}/cases/{caseId}")).Str("assignedAnalystId"));
        Assert.Contains(await Scenario.ListCasesAsync(carol), c => c.Id() == caseId);

        using var dan = await factory.LoginAsAsync(Users.Dan);
        var deniedBefore = (await Scenario.DeniedAuditForAsync(factory, Users.Dan)).Count;
        await Scenario.AssertStatusAsync(HttpStatusCode.Forbidden, dan.GetAsync($"{Paths.Kyc}/cases/{caseId}"), "dan views carol's case");
        await Scenario.AssertStatusAsync(HttpStatusCode.Forbidden, dan.PostAsync($"{Paths.Kyc}/cases/{caseId}/notes", new { text = "sneaky" }), "dan notes carol's case");
        await Scenario.AssertStatusAsync(HttpStatusCode.Forbidden, dan.PostAsync($"{Paths.Kyc}/cases/{caseId}/assign", new { }), "dan steals carol's case");
        Assert.DoesNotContain(await Scenario.ListCasesAsync(dan), c => c.Id() == caseId);
        Assert.Equal(deniedBefore + 3, (await Scenario.DeniedAuditForAsync(factory, Users.Dan)).Count);
    }

    [Fact]
    public async Task AuditorIsReadOnly()
    {
        var caseId = await factory.AnyCaseAsync();
        using var frank = await factory.LoginAsAsync(Users.Frank);
        var detail = await frank.GetJsonAsync($"{Paths.Kyc}/cases/{caseId}");
        Assert.Equal(caseId, detail.Id());

        var deniedBefore = (await Scenario.DeniedAuditForAsync(factory, Users.Frank)).Count;
        await Scenario.AssertStatusAsync(HttpStatusCode.Forbidden, frank.PostAsync($"{Paths.Kyc}/cases/{caseId}/notes", new { text = "auditor note" }), "auditor adds note");
        await Scenario.AssertStatusAsync(HttpStatusCode.Forbidden, frank.PostAsync($"{Paths.Kyc}/cases/{caseId}/assign", new { }), "auditor assigns");
        await Scenario.AssertStatusAsync(HttpStatusCode.Forbidden, frank.PostAsync($"{Paths.Kyc}/cases/{caseId}/reveal", new { }), "auditor reveals PII");
        Assert.Equal(deniedBefore + 3, (await Scenario.DeniedAuditForAsync(factory, Users.Frank)).Count);
    }

    [Fact]
    public async Task UnauthenticatedAppRequestIs401()
    {
        using var anon = factory.CreateAnonymousClient();
        using var response = await anon.GetAsync(new Uri(Paths.Kyc + "/cases", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
