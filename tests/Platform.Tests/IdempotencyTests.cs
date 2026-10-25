using System.Net;
using Platform.Tests.Infrastructure;

namespace Platform.Tests;

public sealed class IdempotencyTests(PlatformFactory factory) : IClassFixture<PlatformFactory>
{
    [Fact]
    public async Task SameKeySameBodyReplaysStoredResponseWithSingleEffect()
    {
        using var alice = await factory.LoginAsAsync(Users.Alice);
        var slug = "idem-" + Guid.NewGuid().ToString("N")[..8];
        var body = Scenario.IntakeBody(slug, "internal", "read-only", title: "Idempotent " + slug);
        var key = PlatformClient.NewKey();

        using var first = await alice.PostAsync(Paths.Builder + "/intakes", body, key);
        var firstBody = await first.Content.ReadAsStringAsync();
        Assert.True(first.IsSuccessStatusCode, firstBody);
        Assert.False(first.Headers.TryGetValues("Idempotency-Replayed", out var h1) && h1.Contains("true"));
        var auditAfterFirst = Db.AuditRowCount(factory);

        using var second = await alice.PostAsync(Paths.Builder + "/intakes", body, key);
        var secondBody = await second.Content.ReadAsStringAsync();

        Assert.Equal(first.StatusCode, second.StatusCode);
        Assert.Equal(firstBody, secondBody);
        Assert.True(second.Headers.TryGetValues("Idempotency-Replayed", out var h2), "missing Idempotency-Replayed header");
        Assert.Equal("true", Assert.Single(h2));
        Assert.Equal(auditAfterFirst, Db.AuditRowCount(factory));

        var intakes = await alice.GetJsonAsync(Paths.Builder + "/intakes");
        Assert.Single(intakes.EnumerateArray(), i => i.Str("appSlug") == slug);
        var intakeId = Json.Parse(firstBody).Id();

        using var frank = await factory.LoginAsAsync(Users.Frank);
        var audit = await Scenario.AllAuditAsync(frank, "barebone-builder");
        Assert.Single(audit, r => r.Str("resourceId") == intakeId && r.Str("outcome") == "success" && r.Str("actorId") == Users.Alice);
    }

    [Fact]
    public async Task SameKeyDifferentBodyIs422AndHasNoEffect()
    {
        using var alice = await factory.LoginAsAsync(Users.Alice);
        var key = PlatformClient.NewKey();
        var slugA = "idem-a-" + Guid.NewGuid().ToString("N")[..8];
        var slugB = "idem-b-" + Guid.NewGuid().ToString("N")[..8];
        await alice.PostJsonAsync(Paths.Builder + "/intakes", Scenario.IntakeBody(slugA, "internal", "read-only"), key);

        using var reused = await alice.PostAsync(Paths.Builder + "/intakes", Scenario.IntakeBody(slugB, "internal", "read-only"), key);
        var text = await reused.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Contains("Idempotency key reused with a different request", text, StringComparison.Ordinal);

        var intakes = await alice.GetJsonAsync(Paths.Builder + "/intakes");
        Assert.DoesNotContain(intakes.EnumerateArray(), i => i.Str("appSlug") == slugB);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    [InlineData("has spaces in key!!")]
    public async Task MissingOrMalformedKeyOnAppMutationIs400(string? key)
    {
        using var alice = await factory.LoginAsAsync(Users.Alice);
        var slug = "idem-nokey-" + Guid.NewGuid().ToString("N")[..8];
        using var response = await alice.SendAsync(HttpMethod.Post, Paths.Builder + "/intakes", Scenario.IntakeBody(slug, "internal", "read-only"), key);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var intakes = await alice.GetJsonAsync(Paths.Builder + "/intakes");
        Assert.DoesNotContain(intakes.EnumerateArray(), i => i.Str("appSlug") == slug);
    }

    [Fact]
    public async Task KeysAreScopedPerUser()
    {
        // Same key used by another user must not replay alice's response to bob.
        using var alice = await factory.LoginAsAsync(Users.Alice);
        using var bob = await factory.LoginAsAsync(Users.Bob);
        var key = PlatformClient.NewKey();
        var slug = "idem-scope-" + Guid.NewGuid().ToString("N")[..8];
        var body = Scenario.IntakeBody(slug, "internal", "read-only");
        var aliceIntake = await alice.PostJsonAsync(Paths.Builder + "/intakes", body, key);

        using var bobResponse = await bob.PostAsync(Paths.Builder + "/intakes", body, key);
        var bobText = await bobResponse.Content.ReadAsStringAsync();
        Assert.True(bobResponse.IsSuccessStatusCode, bobText);
        Assert.False(bobResponse.Headers.TryGetValues("Idempotency-Replayed", out var h) && h.Contains("true"));
        var bobIntake = Json.Parse(bobText);
        Assert.NotEqual(aliceIntake.Id(), bobIntake.Id());
        Assert.Equal(Users.Bob, bobIntake.Str("requesterId"));
    }
}
