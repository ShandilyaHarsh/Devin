using System.Net;
using Barebone.Platform.Abstractions;
using Platform.Tests.Infrastructure;

namespace Platform.Tests;

public sealed class PiiMaskingTests(KycLiveFactory factory) : IClassFixture<KycLiveFactory>
{
    [Fact]
    public async Task CaseDetailIsMaskedRevealIsAuditedAndAuditNeverHoldsRawPii()
    {
        var caseId = await factory.TakeUnassignedCaseAsync();
        using var erin = await factory.LoginAsAsync(Users.Erin);

        using var detailResponse = await erin.GetAsync($"{Paths.Kyc}/cases/{caseId}");
        var detailText = await detailResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = Json.Parse(detailText);

        var auditBefore = Db.AuditRowCount(factory);
        var revealed = await erin.PostJsonAsync($"{Paths.Kyc}/cases/{caseId}/reveal", new { });
        var name = revealed.Str("customerName");
        var dob = revealed.Str("dateOfBirth");
        var ssn = revealed.Str("ssnLast4");
        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.False(string.IsNullOrWhiteSpace(dob));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}", dob);
        Assert.Matches(@"\d{4}$", ssn ?? string.Empty);
        Assert.Equal(auditBefore + 1, Db.AuditRowCount(factory));

        // Detail and list never carry the raw values.
        Assert.DoesNotContain(name!, detailText, StringComparison.Ordinal);
        Assert.DoesNotContain(dob!, detailText, StringComparison.Ordinal);
        Assert.Equal(PiiMask.Name(name), detail.Str("customerName"));
        Assert.Equal("****-**-**", detail.Str("dateOfBirth"));
        Assert.Equal(PiiMask.Last4(ssn![^4..]), detail.Str("ssnLast4"));
        using (var list = await erin.GetAsync(Paths.Kyc + "/cases"))
        {
            var listText = await list.Content.ReadAsStringAsync();
            Assert.DoesNotContain(name!, listText, StringComparison.Ordinal);
            Assert.DoesNotContain(dob!, listText, StringComparison.Ordinal);
        }

        // The reveal is audited as a sensitive read, and the audit row itself contains no raw PII.
        using var frank = await factory.LoginAsAsync(Users.Frank);
        var audit = await Scenario.AllAuditAsync(frank);
        Assert.Contains(audit, r => r.Str("outcome") == "read" && r.Str("actorId") == Users.Erin && r.Str("resourceId") == caseId);

        // Generate more audited changes on the case, then scan the whole trail.
        using var grace = await factory.LoginAsAsync(Users.Grace);
        await grace.PostJsonAsync($"{Paths.Kyc}/cases/{caseId}/assign", new { });
        await grace.PostJsonAsync($"{Paths.Kyc}/cases/{caseId}/notes", new { text = "Checked documents." });
        audit = await Scenario.AllAuditAsync(frank);
        foreach (var row in audit)
        {
            var raw = row.GetRawText();
            Assert.DoesNotContain(name!, raw, StringComparison.Ordinal);
            Assert.DoesNotContain(dob!, raw, StringComparison.Ordinal);
        }

        // Also check the raw database audit table (not just the API projection).
        using var connection = factory.OpenRawDatabase();
        var (table, _) = Db.FindAuditTable(connection);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM \"{table}\" WHERE instr(COALESCE(\"Before\", '') || COALESCE(\"After\", ''), $name) > 0";
        cmd.Parameters.AddWithValue("$name", name);
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    [Theory]
    [InlineData(Users.Frank)]
    [InlineData(Users.Dan)]
    public async Task RevealIsDeniedForAuditorsAndForAnalystsOnOthersCases(string userId)
    {
        var caseId = await factory.TakeUnassignedCaseAsync();
        using var carol = await factory.LoginAsAsync(Users.Carol);
        await carol.PostJsonAsync($"{Paths.Kyc}/cases/{caseId}/assign", new { });

        using var client = await factory.LoginAsAsync(userId);
        using var response = await client.PostAsync($"{Paths.Kyc}/cases/{caseId}/reveal", new { });
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("customerName\":\"" + "x", text, StringComparison.Ordinal);
        Assert.Contains(await Scenario.DeniedAuditForAsync(factory, userId), r => r.Str("resourceId") == caseId || r.Str("action") == "case.reveal-pii");
    }
}

public sealed class PiiMaskTests
{
    [Theory]
    [InlineData("Jane Doe", "J*** D***")]
    [InlineData("Jane", "J***")]
    [InlineData("  Jane   Q  Doe ", "J*** Q*** D***")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void NameKeepsOnlyInitials(string? input, string expected) => Assert.Equal(expected, PiiMask.Name(input));

    [Theory]
    [InlineData("1234", "***-**-1234")]
    [InlineData("9876", "***-**-9876")]
    [InlineData("12345", "***-**-****")]
    [InlineData("123", "***-**-****")]
    [InlineData(null, "***-**-****")]
    public void Last4OnlyEverShowsExactlyFourDigits(string? input, string expected) => Assert.Equal(expected, PiiMask.Last4(input));

    [Fact]
    public void DateNeverShowsAnyPart()
    {
        Assert.Equal("****-**-**", PiiMask.Date(new DateOnly(1980, 5, 17)));
        Assert.Equal("****-**-**", PiiMask.Date(null));
    }

    [Theory]
    [InlineData("jane.doe@example.test", "j***@example.test")]
    [InlineData("a@example.test", "a***@example.test")]
    [InlineData("not-an-email", "***")]
    [InlineData("", "***")]
    [InlineData(null, "***")]
    public void EmailKeepsFirstCharAndDomain(string? input, string expected) => Assert.Equal(expected, PiiMask.Email(input));
}
