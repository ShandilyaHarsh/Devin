using Barebone.Platform.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Platform.Tests.Infrastructure;

namespace Platform.Tests;

public sealed class OutboxRelayTests(CapturingFactory factory) : IClassFixture<CapturingFactory>
{
    private async Task PublishOneAsync(string eventType)
    {
        using var scope = factory.Services.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var admin = scope.ServiceProvider.GetRequiredService<ICatalogAdmin>();
        var slug = "outbox-" + Guid.NewGuid().ToString("N")[..8];
        await uow.ExecuteAsync(new UnitOfWorkContext("probe.publish", "app", slug), async (ws, ct) =>
        {
            await admin.RegisterAsync(ws, new RegisterAppRequest(Manifests.ProbeManifest(slug), Users.Alice, Users.Bianca), ct);
            ws.Publish(eventType, new { marker = eventType });
            return 0;
        });
    }

    private IOutboxRelay Relay => factory.Services.GetRequiredService<IOutboxRelay>();

    [Fact]
    public async Task SequentialRelaysPublishExactlyOnce()
    {
        var eventType = "test.outbox." + Guid.NewGuid().ToString("N");
        await PublishOneAsync(eventType);

        var first = await Relay.RelayPendingAsync(CancellationToken.None);
        await Eventually.TrueAsync(() => factory.Publisher.CountOf(eventType) == 1, "message published");
        var second = await Relay.RelayPendingAsync(CancellationToken.None);

        Assert.InRange(first, 0, int.MaxValue);
        Assert.Equal(0, second);
        await Task.Delay(2500); // let the 2 s background relay tick at least once more
        Assert.Equal(1, factory.Publisher.CountOf(eventType));

        var message = factory.Publisher.Messages.Single(m => m.EventType == eventType);
        Assert.Contains(eventType, message.PayloadJson, StringComparison.Ordinal);
        Assert.True(message.Id > 0);
    }

    [Fact]
    public async Task ConcurrentRelaysPublishEachRowOnce()
    {
        var eventType = "test.outbox.concurrent." + Guid.NewGuid().ToString("N");
        await PublishOneAsync(eventType);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Relay.RelayPendingAsync(CancellationToken.None))));
        await Eventually.TrueAsync(() => factory.Publisher.CountOf(eventType) == 1, "message published");
        await Task.Delay(2500);

        Assert.Equal(1, factory.Publisher.CountOf(eventType));
        Assert.True(results.Sum() <= 1 + factory.Publisher.Messages.Count(m => m.EventType != eventType), "relays claimed more rows than exist: " + string.Join(",", results));
        Assert.Equal(0, await Relay.RelayPendingAsync(CancellationToken.None));
    }
}
