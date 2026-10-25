using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Platform.Tests.Infrastructure;

public static class Users
{
    public const string Alice = "u-alice";
    public const string Bob = "u-bob";
    public const string Bianca = "u-bianca";
    public const string Carol = "u-carol";
    public const string Dan = "u-dan";
    public const string Erin = "u-erin";
    public const string Grace = "u-grace";
    public const string Frank = "u-frank";
    public const string Mallory = "u-mallory";
}

public static class Paths
{
    public const string Builder = "/api/apps/barebone-builder";
    public const string Kyc = "/api/apps/kyc-case-reviewer";
    public const string KycSlug = "kyc-case-reviewer";
}

/// <summary>Business flows driven only through the public HTTP API (docs/prototype-plan.md section 4).</summary>
public static class Scenario
{
    public static object IntakeBody(
        string slug,
        string classification = "restricted",
        string access = "write",
        string? title = null) => new
        {
            title = title ?? "Intake for " + slug,
            appSlug = slug,
            problem = "Manual spreadsheet process is slow and error prone.",
            users = "Operations team",
            dataTouched = "Synthetic customer records",
            dataClassification = classification,
            access,
            hoursSavedPerUserPerWeek = 4,
            numberOfUsers = 10,
        };

    public static async Task<JsonElement> SubmitIntakeAsync(PlatformClient requester, string slug, string classification = "restricted", string access = "write") =>
        await requester.PostJsonAsync(Paths.Builder + "/intakes", IntakeBody(slug, classification, access));

    public static async Task SetStageAsync(PlatformClient builder, string slug, string stage) =>
        await builder.PostJsonAsync($"{Paths.Builder}/apps/{slug}/stage", new { stage });

    public static async Task SetAccessListAsync(PlatformClient builder, string slug, params string[] userIds) =>
        await builder.PutJsonAsync($"{Paths.Builder}/apps/{slug}/access-list", new { userIds });

    /// <summary>Alice submits the KYC intake (restricted, write); Bianca approves (registers in builder-only).</summary>
    public static async Task RegisterKycAsync(PlatformFactory factory)
    {
        using var alice = await factory.LoginAsAsync(Users.Alice);
        var intake = await SubmitIntakeAsync(alice, Paths.KycSlug);
        using var bianca = await factory.LoginAsAsync(Users.Bianca);
        await bianca.PostJsonAsync($"{Paths.Builder}/intakes/{intake.Id()}/approve", new { });
    }

    /// <summary>Register + promote KYC to the given stage (and access list when live).</summary>
    public static async Task PromoteKycAsync(PlatformFactory factory, string stage, params string[] accessList)
    {
        await RegisterKycAsync(factory);
        using var bianca = await factory.LoginAsAsync(Users.Bianca);
        if (stage != "builder-only")
        {
            await SetStageAsync(bianca, Paths.KycSlug, stage);
        }

        if (accessList.Length > 0)
        {
            await SetAccessListAsync(bianca, Paths.KycSlug, accessList);
        }
    }

    public static async Task<List<JsonElement>> ListCasesAsync(PlatformClient client)
    {
        var cases = await client.GetJsonAsync(Paths.Kyc + "/cases");
        Assert.Equal(JsonValueKind.Array, cases.ValueKind);
        return [.. cases.EnumerateArray()];
    }

    /// <summary>All audit rows (newest first), paging with beforeId. Reader must be Auditor/Builder.</summary>
    public static async Task<List<JsonElement>> AllAuditAsync(PlatformClient reader, string? app = null)
    {
        var rows = new List<JsonElement>();
        long? beforeId = null;
        for (var page = 0; page < 200; page++)
        {
            var query = $"/api/platform/audit?limit=50" + (app is null ? string.Empty : "&app=" + app) +
                (beforeId is null ? string.Empty : "&beforeId=" + beforeId);
            var batch = await reader.GetJsonAsync(query);
            var items = batch.EnumerateArray().ToList();
            if (items.Count == 0)
            {
                break;
            }

            rows.AddRange(items);
            var minId = items.Min(i => i.GetProperty("id").GetInt64());
            if (beforeId is not null && minId >= beforeId)
            {
                break;
            }

            beforeId = minId;
        }

        return rows;
    }

    public static async Task<List<JsonElement>> DeniedAuditForAsync(PlatformFactory factory, string actorId)
    {
        using var frank = await factory.LoginAsAsync(Users.Frank);
        return [.. (await AllAuditAsync(frank)).Where(r => r.Str("actorId") == actorId && r.Str("outcome") == "denied")];
    }

    public static async Task AssertStatusAsync(HttpStatusCode expected, Task<HttpResponseMessage> call, string what)
    {
        using var response = await call;
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(expected == response.StatusCode, $"{what}: expected {(int)expected} but got {(int)response.StatusCode}: {body}");
    }
}

/// <summary>Direct, table-agnostic inspection of the SQLite file (table names are implementation details).</summary>
public static class Db
{
    public static Dictionary<string, long> RowCounts(PlatformFactory factory)
    {
        using var connection = factory.OpenRawDatabase();
        var tables = new List<string>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
            counts[table] = (long)cmd.ExecuteScalar()!;
        }

        return counts;
    }

    public static List<string> Columns(SqliteConnection connection, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info($t)";
        cmd.Parameters.AddWithValue("$t", table);
        using var reader = cmd.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    /// <summary>The audit table is the one with both a prevHash and a hash column.</summary>
    public static (string Table, List<string> Columns) FindAuditTable(SqliteConnection connection)
    {
        foreach (var table in RowTables(connection))
        {
            var columns = Columns(connection, table);
            if (columns.Any(c => c.Equals("PrevHash", StringComparison.OrdinalIgnoreCase) || c.Equals("prev_hash", StringComparison.OrdinalIgnoreCase)) &&
                columns.Any(c => c.Equals("Hash", StringComparison.OrdinalIgnoreCase)))
            {
                return (table, columns);
            }
        }

        throw new InvalidOperationException("No audit table (with PrevHash + Hash columns) found in the SQLite database.");
    }

    public static long AuditRowCount(PlatformFactory factory)
    {
        using var connection = factory.OpenRawDatabase();
        var (table, _) = FindAuditTable(connection);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
        return (long)cmd.ExecuteScalar()!;
    }

    private static List<string> RowTables(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        using var reader = cmd.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }
}
