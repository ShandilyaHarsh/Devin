using System.Net;
using Platform.Tests.Infrastructure;

namespace Platform.Tests;

public sealed class AuditChainTests(PlatformFactory factory) : IClassFixture<PlatformFactory>
{
    [Fact]
    public async Task ChainVerifiesThenDetectsDirectDatabaseTampering()
    {
        using var alice = await factory.LoginAsAsync(Users.Alice);
        using var bianca = await factory.LoginAsAsync(Users.Bianca);
        using var mallory = await factory.LoginAsAsync(Users.Mallory);
        var i1 = await Scenario.SubmitIntakeAsync(alice, "chain-a-" + Guid.NewGuid().ToString("N")[..6], "internal", "read-only");
        await Scenario.SubmitIntakeAsync(alice, "chain-b-" + Guid.NewGuid().ToString("N")[..6], "internal", "read-only");
        await bianca.PostJsonAsync($"{Paths.Builder}/intakes/{i1.Id()}/approve", new { });
        using (await mallory.GetAsync(Paths.Builder + "/intakes"))
        {
        }

        using var frank = await factory.LoginAsAsync(Users.Frank);
        var ok = await frank.GetJsonAsync("/api/platform/audit/verify");
        Assert.True(ok.GetProperty("valid").GetBoolean(), ok.GetRawText());
        Assert.True(ok.GetProperty("checked").GetInt64() >= 4, ok.GetRawText());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, ok.GetProperty("firstBrokenId").ValueKind);

        var rows = await Scenario.AllAuditAsync(frank);
        Assert.Contains(rows, r => r.Str("outcome") == "denied" && r.Str("actorId") == Users.Mallory);
        var ordered = rows.OrderBy(r => r.GetProperty("id").GetInt64()).ToList();
        Assert.Equal(new string('0', 64), ordered[0].Str("prevHash"));
        for (var i = 1; i < ordered.Count; i++)
        {
            Assert.Equal(ordered[i - 1].Str("hash"), ordered[i].Str("prevHash"));
            Assert.Matches("^[0-9a-f]{64}$", ordered[i].Str("hash") ?? string.Empty);
        }

        var target = ordered[ordered.Count / 2].GetProperty("id").GetInt64();
        using (var connection = factory.OpenRawDatabase())
        {
            var (table, columns) = Db.FindAuditTable(connection);
            var idColumn = columns.First(c => c.Equals("Id", StringComparison.OrdinalIgnoreCase));
            var actionColumn = columns.First(c => c.Equals("Action", StringComparison.OrdinalIgnoreCase));
            using var update = connection.CreateCommand();
            update.CommandText = $"UPDATE \"{table}\" SET \"{actionColumn}\" = \"{actionColumn}\" || '.tampered' WHERE \"{idColumn}\" = $id";
            update.Parameters.AddWithValue("$id", target);
            Assert.Equal(1, update.ExecuteNonQuery());
        }

        var broken = await frank.GetJsonAsync("/api/platform/audit/verify");
        Assert.False(broken.GetProperty("valid").GetBoolean(), broken.GetRawText());
        Assert.Equal(target, broken.GetProperty("firstBrokenId").GetInt64());
        Assert.False(string.IsNullOrWhiteSpace(broken.Str("reason")));
    }

    [Theory]
    [InlineData("PUT", "/api/platform/audit")]
    [InlineData("DELETE", "/api/platform/audit")]
    [InlineData("PUT", "/api/platform/audit/1")]
    [InlineData("DELETE", "/api/platform/audit/1")]
    [InlineData("PATCH", "/api/platform/audit/1")]
    [InlineData("POST", "/api/platform/audit")]
    [InlineData("DELETE", "/api/platform/audit/verify")]
    public async Task NoHttpRouteMutatesAuditRows(string method, string path)
    {
        using var frank = await factory.LoginAsAsync(Users.Frank);
        var before = Db.AuditRowCount(factory);
        using var response = await frank.SendAsync(new HttpMethod(method), path, new { action = "x" }, PlatformClient.NewKey());
        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"{method} {path} returned {(int)response.StatusCode}");
        Assert.True(Db.AuditRowCount(factory) >= before, "audit rows must never disappear");
    }

    [Theory]
    [InlineData(Users.Carol)]
    [InlineData(Users.Alice)]
    [InlineData(Users.Mallory)]
    public async Task NonAuditorsCannotReadOrVerifyAudit(string userId)
    {
        using var client = await factory.LoginAsAsync(userId);
        await Scenario.AssertStatusAsync(HttpStatusCode.Forbidden, client.GetAsync("/api/platform/audit"), userId + " audit list");
        await Scenario.AssertStatusAsync(HttpStatusCode.Forbidden, client.GetAsync("/api/platform/audit/verify"), userId + " audit verify");
    }

    [Fact]
    public async Task BuildersMayVerify()
    {
        using var bob = await factory.LoginAsAsync(Users.Bob);
        var result = await bob.GetJsonAsync("/api/platform/audit/verify");
        Assert.True(result.TryGetProperty("valid", out _));
    }
}
