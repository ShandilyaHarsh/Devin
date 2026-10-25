using Barebone.Platform.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Platform.Tests.Infrastructure;

namespace Platform.Tests;

public sealed class AtomicityTests(CapturingFactory factory) : IClassFixture<CapturingFactory>
{
    private sealed class ProbeFailureException(string message) : Exception(message);

    [Fact]
    public async Task FailedUnitOfWorkPersistsNothing()
    {
        var slug = "atom-fail-" + Guid.NewGuid().ToString("N")[..8];
        var eventType = "test.atomicity." + slug;
        using var scope = factory.Services.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var admin = scope.ServiceProvider.GetRequiredService<ICatalogAdmin>();
        var catalog = scope.ServiceProvider.GetRequiredService<ICatalog>();
        var relay = scope.ServiceProvider.GetRequiredService<IOutboxRelay>();

        var before = Db.RowCounts(factory);
        await Assert.ThrowsAsync<ProbeFailureException>(() => uow.ExecuteAsync<int>(
            new UnitOfWorkContext("probe.register", "app", slug),
            async (writeScope, ct) =>
            {
                await admin.RegisterAsync(writeScope, new RegisterAppRequest(Manifests.ProbeManifest(slug), Users.Alice, Users.Bianca), ct);
                writeScope.Publish(eventType, new { slug });
                throw new ProbeFailureException("boom after business write + publish");
            }));
        var after = Db.RowCounts(factory);

        Assert.Equal(before, after);
        Assert.Null(await catalog.GetAsync(slug, CancellationToken.None));
        await relay.RelayPendingAsync(CancellationToken.None);
        Assert.Equal(0, factory.Publisher.CountOf(eventType));
    }

    [Fact]
    public async Task SuccessfulUnitOfWorkWritesBusinessRowAuditRowAndOutboxTogether()
    {
        var slug = "atom-ok-" + Guid.NewGuid().ToString("N")[..8];
        var eventType = "test.atomicity." + slug;
        using var scope = factory.Services.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var admin = scope.ServiceProvider.GetRequiredService<ICatalogAdmin>();
        var catalog = scope.ServiceProvider.GetRequiredService<ICatalog>();
        var relay = scope.ServiceProvider.GetRequiredService<IOutboxRelay>();

        var before = Db.RowCounts(factory);
        var auditBefore = Db.AuditRowCount(factory);
        var result = await uow.ExecuteAsync(
            new UnitOfWorkContext("probe.register", "app", slug),
            async (writeScope, ct) =>
            {
                var entry = await admin.RegisterAsync(writeScope, new RegisterAppRequest(Manifests.ProbeManifest(slug), Users.Alice, Users.Bianca), ct);
                writeScope.Publish(eventType, new { slug });
                return entry;
            });
        var after = Db.RowCounts(factory);

        Assert.Equal(slug, result.Slug);
        var stored = await catalog.GetAsync(slug, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(AppStage.BuilderOnly, stored.Stage);
        Assert.Equal(auditBefore + 1, Db.AuditRowCount(factory));
        var grownTables = after.Where(kv => kv.Value > before.GetValueOrDefault(kv.Key)).Select(kv => kv.Key).ToList();
        Assert.True(grownTables.Count >= 3, "expected catalog, audit and outbox tables to grow; grew: " + string.Join(", ", grownTables));

        await relay.RelayPendingAsync(CancellationToken.None);
        await Eventually.TrueAsync(() => factory.Publisher.CountOf(eventType) == 1, "outbox message published after commit");
    }

    [Fact]
    public async Task FailedHttpMutationLeavesNoIdempotencyRecordOrBusinessChange()
    {
        // KYC code declares access: write; an intake approved as read-only must fail registration (422) inside the UoW.
        using var alice = await factory.LoginAsAsync(Users.Alice);
        var intake = await Scenario.SubmitIntakeAsync(alice, Paths.KycSlug, classification: "restricted", access: "read-only");
        using var bianca = await factory.LoginAsAsync(Users.Bianca);
        var key = PlatformClient.NewKey();

        var before = Db.RowCounts(factory);
        using (var first = await bianca.PostAsync($"{Paths.Builder}/intakes/{intake.Id()}/approve", new { }, key))
        {
            Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, first.StatusCode);
        }

        var after = Db.RowCounts(factory);
        var grown = after.Where(kv => kv.Value != before.GetValueOrDefault(kv.Key)).ToList();

        // Only a 'denied'-style audit row may legitimately be written outside the failed UoW; business, idempotency, outbox must not.
        Assert.True(grown.Count <= 1, "rows changed in: " + string.Join(", ", grown.Select(g => g.Key)));
        var stillSubmitted = await bianca.GetJsonAsync($"{Paths.Builder}/intakes/{intake.Id()}");
        Assert.Equal("submitted", stillSubmitted.Str("status"));
        using var catalogResponse = await bianca.GetAsync("/api/platform/catalog/" + Paths.KycSlug);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, catalogResponse.StatusCode);

        // No idempotency record was stored: retrying with the same key re-executes (not a replay).
        using var retry = await bianca.PostAsync($"{Paths.Builder}/intakes/{intake.Id()}/approve", new { }, key);
        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, retry.StatusCode);
        Assert.False(retry.Headers.TryGetValues("Idempotency-Replayed", out var replayed) && replayed.Contains("true"));
    }
}
