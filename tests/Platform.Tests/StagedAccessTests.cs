using System.Net;
using Platform.Tests.Infrastructure;

namespace Platform.Tests;

public sealed class StagedAccessTests(PlatformFactory factory) : IClassFixture<PlatformFactory>
{
    private async Task<string> DeniedAsync(PlatformClient client, string what)
    {
        using var response = await client.GetAsync(Paths.Kyc + "/cases");
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{what}: expected 403 got {(int)response.StatusCode}: {text}");
        return text;
    }

    private static async Task AllowedAsync(PlatformClient client, string what)
    {
        using var response = await client.GetAsync(Paths.Kyc + "/cases");
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{what}: expected 200 got {(int)response.StatusCode}: {text}");
    }

    private static async Task<System.Text.Json.JsonElement> AccessAsync(PlatformClient client) =>
        await client.GetJsonAsync($"/api/platform/apps/{Paths.KycSlug}/access");

    [Fact]
    public async Task StageGateFollowsCatalogThroughTheLifecycle()
    {
        using var alice = await factory.LoginAsAsync(Users.Alice);
        using var bob = await factory.LoginAsAsync(Users.Bob);
        using var bianca = await factory.LoginAsAsync(Users.Bianca);
        using var carol = await factory.LoginAsAsync(Users.Carol);
        using var dan = await factory.LoginAsAsync(Users.Dan);

        // 1. Code exists but the app is not registered in the catalog: nobody gets in, not even a Builder.
        var body = await DeniedAsync(bob, "unregistered, builder");
        Assert.Contains("app-not-registered", body, StringComparison.Ordinal);
        var access = await AccessAsync(bob);
        Assert.False(access.GetProperty("allowed").GetBoolean());
        Assert.Contains("app-not-registered", access.Str("reason") ?? string.Empty, StringComparison.Ordinal);
        await AssertDeniedAuditedAsync(Users.Bob, "app-not-registered");

        // 2. builder-only after Builder approval.
        var intake = await Scenario.SubmitIntakeAsync(alice, Paths.KycSlug);
        await bianca.PostJsonAsync($"{Paths.Builder}/intakes/{intake.Id()}/approve", new { });
        var entry = await bob.GetJsonAsync("/api/platform/catalog/" + Paths.KycSlug);
        Assert.Equal("builder-only", entry.Str("stage"));
        Assert.Equal(Users.Alice, entry.Str("requesterId"));
        Assert.Equal(Users.Bianca, entry.Str("approvedBy"));
        await DeniedAsync(alice, "builder-only, requester");
        await DeniedAsync(carol, "builder-only, analyst");
        await AllowedAsync(bob, "builder-only, builder");
        Assert.Equal("builder-only", (await AccessAsync(bob)).Str("stage"));
        Assert.True((await AccessAsync(bob)).GetProperty("allowed").GetBoolean());
        Assert.False((await AccessAsync(alice)).GetProperty("allowed").GetBoolean());

        // 3. demo: Builders + the requester (as demo viewer); others still denied.
        await Scenario.SetStageAsync(bianca, Paths.KycSlug, "demo");
        await AllowedAsync(alice, "demo, requester");
        await AllowedAsync(bob, "demo, builder");
        await DeniedAsync(carol, "demo, analyst");
        await DeniedAsync(dan, "demo, other analyst");

        // 4. live: only the catalog access list.
        await Scenario.SetStageAsync(bianca, Paths.KycSlug, "live");
        await Scenario.SetAccessListAsync(bianca, Paths.KycSlug, Users.Carol);
        await AllowedAsync(carol, "live, on access list");
        await DeniedAsync(dan, "live, not on access list");
        await DeniedAsync(alice, "live, requester not on list");
        await DeniedAsync(bob, "live, builder not on list");
        Assert.True((await AccessAsync(carol)).GetProperty("allowed").GetBoolean());
        Assert.False((await AccessAsync(dan)).GetProperty("allowed").GetBoolean());

        // Each denial is audited with outcome 'denied'.
        await AssertDeniedAuditedAsync(Users.Alice, null, minimum: 2);
        await AssertDeniedAuditedAsync(Users.Carol, null, minimum: 2);
        await AssertDeniedAuditedAsync(Users.Dan, null, minimum: 2);
        await AssertDeniedAuditedAsync(Users.Bob, null, minimum: 2);
    }

    [Fact]
    public async Task WildcardAccessListIsRejectedForRestrictedApps()
    {
        using var alice = await factory.LoginAsAsync(Users.Alice);
        using var bianca = await factory.LoginAsAsync(Users.Bianca);
        var slug = "wild-" + Guid.NewGuid().ToString("N")[..6];
        var intake = await Scenario.SubmitIntakeAsync(alice, slug, "restricted", "read-only");
        await bianca.PostJsonAsync($"{Paths.Builder}/intakes/{intake.Id()}/approve", new { });

        using var response = await bianca.PutAsync($"{Paths.Builder}/apps/{slug}/access-list", new { userIds = new[] { "*" } });
        Assert.False(response.IsSuccessStatusCode, "'*' must not be accepted for a restricted app");
        var entry = await bianca.GetJsonAsync("/api/platform/catalog/" + slug);
        Assert.DoesNotContain(entry.GetProperty("accessList").EnumerateArray(), v => v.GetString() == "*");
    }

    private async Task AssertDeniedAuditedAsync(string actor, string? reasonContains, int minimum = 1)
    {
        var rows = (await Scenario.DeniedAuditForAsync(factory, actor))
            .Where(r => r.Str("appSlug") == Paths.KycSlug)
            .Where(r => reasonContains is null || (r.Str("reason") ?? string.Empty).Contains(reasonContains, StringComparison.Ordinal))
            .ToList();
        Assert.True(rows.Count >= minimum, $"expected >= {minimum} denied audit rows for {actor} (reason~{reasonContains}), got {rows.Count}");
    }
}
