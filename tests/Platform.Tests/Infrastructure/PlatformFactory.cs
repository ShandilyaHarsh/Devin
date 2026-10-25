using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Platform.Tests.Infrastructure;

/// <summary>
/// Host under test. One temp SQLite file + random audit chain key per factory instance (i.e. per test class).
/// </summary>
public class PlatformFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings;
    private readonly List<Action<IServiceCollection>> _serviceOverrides = [];

    public PlatformFactory()
    {
        DatabasePath = Path.Combine(Path.GetTempPath(), "barebone-tests-" + Guid.NewGuid().ToString("N") + ".db");
        _settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:Platform"] = $"Data Source={DatabasePath}",
            ["Platform:AuditChainKey"] = RandomHex(32),
            ["Platform:RateLimits:LoginPerMinute"] = "100000",
            ["Platform:RateLimits:MutationsPerMinute"] = "100000",
            ["Platform:Session:IdleMinutes"] = "30",
            ["Platform:Session:AbsoluteHours"] = "8",
        };
    }

    public string DatabasePath { get; }

    public Uri BaseAddress { get; } = new("https://localhost");

    /// <summary>Override (or with null value, blank out) a configuration value. Call before the first client is created.</summary>
    public PlatformFactory WithSetting(string key, string? value)
    {
        _settings[key] = value;
        return this;
    }

    public PlatformFactory WithServices(Action<IServiceCollection> configure)
    {
        _serviceOverrides.Add(configure);
        return this;
    }

    public static string RandomHex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    public SqliteConnection OpenRawDatabase()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString());
        connection.Open();
        return connection;
    }

    public HttpClient CreateAnonymousClient(bool handleCookies = true) =>
        CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = BaseAddress,
            HandleCookies = handleCookies,
            AllowAutoRedirect = false,
        });

    public async Task<PlatformClient> LoginAsAsync(string userId)
    {
        var http = CreateAnonymousClient();
        var csrf = await PlatformClient.FetchCsrfTokenAsync(http);
        using (var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login"))
        {
            login.Headers.Add("X-CSRF-Token", csrf);
            login.Content = JsonContent.Create(new { userId });
            using var response = await http.SendAsync(login);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"login as {userId} failed: {(int)response.StatusCode} {body}");
        }

        // Session id (and therefore the CSRF token) rotates at login; re-fetch like the SPA does.
        var client = new PlatformClient(http, userId);
        await client.RefreshCsrfAsync();
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value ?? string.Empty);
        }

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(_settings));
        builder.ConfigureTestServices(services =>
        {
            foreach (var configure in _serviceOverrides)
            {
                configure(services);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
            {
                try
                {
                    File.Delete(DatabasePath + suffix);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
