using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Platform.Tests.Infrastructure;

namespace Platform.Tests;

public sealed partial class CsrfAndSessionTests(PlatformFactory factory) : IClassFixture<PlatformFactory>
{
    [GeneratedRegex("^[A-Za-z0-9_\\-+/=.]{32,}$")]
    private static partial Regex TokenLike();

    [Fact]
    public async Task LoginWithoutOrWithWrongCsrfTokenIs403()
    {
        using var anon = factory.CreateAnonymousClient();
        var token = await PlatformClient.FetchCsrfTokenAsync(anon);

        using (var missing = await anon.PostAsJsonAsync(new Uri("/api/auth/login", UriKind.Relative), new { userId = Users.Bob }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
            Assert.Contains("CSRF token missing or invalid", await missing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using var wrong = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { userId = Users.Bob }) };
        wrong.Headers.Add("X-CSRF-Token", token + "x");
        using (var response = await anon.SendAsync(wrong))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        var session = await anon.GetFromJsonAsync<System.Text.Json.JsonElement>(new Uri("/api/session", UriKind.Relative));
        Assert.False(session.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task AuthenticatedMutationRequiresMatchingCsrfToken()
    {
        using var alice = await factory.LoginAsAsync(Users.Alice);
        using var bob = await factory.LoginAsAsync(Users.Bob);
        var slug = "csrf-" + Guid.NewGuid().ToString("N")[..6];
        var body = Scenario.IntakeBody(slug, "internal", "read-only");

        await Scenario.AssertStatusAsync(HttpStatusCode.Forbidden, alice.SendAsync(HttpMethod.Post, Paths.Builder + "/intakes", body, PlatformClient.NewKey(), includeCsrf: false), "no csrf");
        await Scenario.AssertStatusAsync(HttpStatusCode.Forbidden, alice.SendAsync(HttpMethod.Post, Paths.Builder + "/intakes", body, PlatformClient.NewKey(), csrfOverride: "not-the-token"), "wrong csrf");
        await Scenario.AssertStatusAsync(HttpStatusCode.Forbidden, alice.SendAsync(HttpMethod.Post, Paths.Builder + "/intakes", body, PlatformClient.NewKey(), csrfOverride: bob.CsrfToken), "another session's csrf");
        await Scenario.AssertStatusAsync(HttpStatusCode.Forbidden, alice.SendAsync(HttpMethod.Post, "/api/auth/logout", null, null, includeCsrf: false), "logout without csrf");
        Assert.DoesNotContain((await alice.GetJsonAsync(Paths.Builder + "/intakes")).EnumerateArray(), i => i.Str("appSlug") == slug);

        using var ok = await alice.PostAsync(Paths.Builder + "/intakes", body);
        Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SessionCookieIsHostPrefixedHttpOnlySecureStrictAndNotInBody()
    {
        using var http = factory.CreateAnonymousClient(handleCookies: false);
        using var sessionResponse = await http.GetAsync(new Uri("/api/session", UriKind.Relative));
        var preCookies = CookiePairs(sessionResponse);
        var csrf = Json.Parse(await sessionResponse.Content.ReadAsStringAsync()).Str("csrfToken")!;

        var (cookie, body, _) = await LoginRawAsync(http, preCookies, csrf, Users.Bob);
        var attributes = cookie.Split(';').Select(a => a.Trim()).ToList();
        var value = attributes[0]["__Host-session=".Length..];
        Assert.Contains(attributes, a => a.Equals("HttpOnly", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(attributes, a => a.Equals("Secure", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(attributes, a => a.Equals("SameSite=Strict", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(attributes, a => a.Equals("Path=/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(attributes, a => a.StartsWith("Domain=", StringComparison.OrdinalIgnoreCase));
        Assert.True(value.Length >= 43, "session id should carry >= 256 bits: " + value.Length);

        var json = Json.Parse(body);
        Assert.True(json.GetProperty("authenticated").GetBoolean());
        Assert.Equal(Users.Bob, json.GetProperty("user").Str("id"));
        Assert.DoesNotContain(value, body, StringComparison.Ordinal);
        foreach (var (path, text) in JsonText.Strings(json))
        {
            if (path == "$.csrfToken")
            {
                continue;
            }

            Assert.False(TokenLike().IsMatch(text), $"token-like value at {path} in login response");
        }
    }

    [Fact]
    public async Task LoginRotatesSessionIdAndOldIdIsDead()
    {
        using var http = factory.CreateAnonymousClient(handleCookies: false);
        using var sessionResponse = await http.GetAsync(new Uri("/api/session", UriKind.Relative));
        var cookies = CookiePairs(sessionResponse);
        var csrf = Json.Parse(await sessionResponse.Content.ReadAsStringAsync()).Str("csrfToken")!;

        var (first, _, _) = await LoginRawAsync(http, cookies, csrf, Users.Bob);
        var firstPair = first.Split(';')[0];
        var csrf2 = Json.Parse(await GetSessionRawAsync(http, [.. cookies, firstPair])).Str("csrfToken")!;
        var (second, _, _) = await LoginRawAsync(http, [.. cookies, firstPair], csrf2, Users.Bianca);
        var secondPair = second.Split(';')[0];
        Assert.NotEqual(firstPair, secondPair);

        var old = Json.Parse(await GetSessionRawAsync(http, [.. cookies, firstPair]));
        Assert.False(old.GetProperty("authenticated").GetBoolean(), "pre-rotation session id must no longer authenticate");
        var current = Json.Parse(await GetSessionRawAsync(http, [.. cookies, secondPair]));
        Assert.Equal(Users.Bianca, current.GetProperty("user").Str("id"));
    }

    [Fact]
    public async Task LogoutEndsSession()
    {
        using var bob = await factory.LoginAsAsync(Users.Bob);
        using (var logout = await bob.SendAsync(HttpMethod.Post, "/api/auth/logout", null, null))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }

        var session = await bob.GetJsonAsync("/api/session");
        Assert.False(session.GetProperty("authenticated").GetBoolean());
        await Scenario.AssertStatusAsync(HttpStatusCode.Unauthorized, bob.GetAsync(Paths.Builder + "/intakes"), "after logout");
    }

    [Fact]
    public async Task UnknownUserCannotLogIn()
    {
        using var anon = factory.CreateAnonymousClient();
        var token = await PlatformClient.FetchCsrfTokenAsync(anon);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { userId = "u-nobody" }) };
        request.Headers.Add("X-CSRF-Token", token);
        using var response = await anon.SendAsync(request);
        Assert.False(response.IsSuccessStatusCode);
        var session = await anon.GetFromJsonAsync<System.Text.Json.JsonElement>(new Uri("/api/session", UriKind.Relative));
        Assert.False(session.GetProperty("authenticated").GetBoolean());
    }

    internal static List<string> CookiePairs(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? [.. values.Select(v => v.Split(';')[0])] : [];

    private static async Task<string> GetSessionRawAsync(HttpClient http, IEnumerable<string> cookies)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/session");
        request.Headers.Add("Cookie", string.Join("; ", cookies));
        using var response = await http.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<(string SessionCookie, string Body, HttpStatusCode Status)> LoginRawAsync(HttpClient http, IEnumerable<string> cookies, string csrf, string userId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { userId }) };
        request.Headers.Add("Cookie", string.Join("; ", cookies));
        request.Headers.Add("X-CSRF-Token", csrf);
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var setCookies), "login must set the session cookie");
        var session = setCookies.SingleOrDefault(c => c.StartsWith("__Host-session=", StringComparison.Ordinal));
        Assert.NotNull(session);
        return (session, body, response.StatusCode);
    }
}

public sealed class FakeClockFactory : PlatformFactory
{
    public FakeClockFactory()
    {
        WithServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
}

public sealed class SessionExpiryTests(FakeClockFactory factory) : IClassFixture<FakeClockFactory>
{
    [Fact]
    public async Task IdleTimeoutExpiresSession()
    {
        using var bob = await factory.LoginAsAsync(Users.Bob);
        Assert.True((await bob.GetJsonAsync("/api/session")).GetProperty("authenticated").GetBoolean());
        await Scenario.AssertStatusAsync(HttpStatusCode.OK, bob.GetAsync(Paths.Builder + "/intakes"), "fresh session");

        factory.Clock.Advance(TimeSpan.FromMinutes(31));

        Assert.False((await bob.GetJsonAsync("/api/session")).GetProperty("authenticated").GetBoolean());
        await Scenario.AssertStatusAsync(HttpStatusCode.Unauthorized, bob.GetAsync(Paths.Builder + "/intakes"), "idle-expired session");
    }

    [Fact]
    public async Task ActivitySlidesIdleTimeoutButAbsoluteLimitStillApplies()
    {
        using var bob = await factory.LoginAsAsync(Users.Bob);
        for (var i = 0; i < 6; i++)
        {
            factory.Clock.Advance(TimeSpan.FromMinutes(20));
            await Scenario.AssertStatusAsync(HttpStatusCode.OK, bob.GetAsync(Paths.Builder + "/intakes"), $"active after {(i + 1) * 20} min");
        }

        for (var i = 0; i < 20; i++)
        {
            factory.Clock.Advance(TimeSpan.FromMinutes(20));
            using var _ = await bob.GetAsync(Paths.Builder + "/intakes");
        }

        // > 8h since login even though never idle for 30 min.
        Assert.False((await bob.GetJsonAsync("/api/session")).GetProperty("authenticated").GetBoolean());
        await Scenario.AssertStatusAsync(HttpStatusCode.Unauthorized, bob.GetAsync(Paths.Builder + "/intakes"), "absolute-expired session");
    }
}
