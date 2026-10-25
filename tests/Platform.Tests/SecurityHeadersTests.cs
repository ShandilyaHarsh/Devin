using System.Net;
using System.Net.Http.Json;
using Platform.Tests.Infrastructure;

namespace Platform.Tests;

public sealed class SecurityHeadersTests(PlatformFactory factory) : IClassFixture<PlatformFactory>
{
    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var v) ? string.Join(",", v)
        : response.Content.Headers.TryGetValues(name, out var c) ? string.Join(",", c)
        : throw new Xunit.Sdk.XunitException($"missing header {name} on {(int)response.StatusCode} {response.RequestMessage?.RequestUri}");

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        var csp = Header(response, "Content-Security-Policy");
        var directives = csp.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToDictionary(d => d.Split(' ')[0], d => d, StringComparer.OrdinalIgnoreCase);
        Assert.True(directives.TryGetValue("script-src", out var scriptSrc), csp);
        Assert.Equal("script-src 'self'", scriptSrc);
        Assert.DoesNotContain("unsafe-inline", scriptSrc, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", scriptSrc, StringComparison.Ordinal);
        Assert.Equal("default-src 'self'", directives["default-src"]);
        Assert.Equal("frame-ancestors 'none'", directives["frame-ancestors"]);
        Assert.Equal("object-src 'none'", directives["object-src"]);

        Assert.Contains("max-age=31536000", Header(response, "Strict-Transport-Security"), StringComparison.Ordinal);
        Assert.Contains("includeSubDomains", Header(response, "Strict-Transport-Security"), StringComparison.Ordinal);
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal("same-origin", Header(response, "Cross-Origin-Opener-Policy"));
        Assert.Contains("camera=()", Header(response, "Permissions-Policy"), StringComparison.Ordinal);
    }

    private static async Task AssertSafeProblemAsync(HttpResponseMessage response, int expectedStatus)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = Json.Parse(text);
        Assert.False(string.IsNullOrWhiteSpace(json.Str("title")), text);
        Assert.Equal(expectedStatus, json.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(json.Str("correlationId")), "problem must carry correlationId: " + text);
        Assert.DoesNotContain("   at ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", text, StringComparison.Ordinal);
        Assert.DoesNotContain("stackTrace", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".cs:line", text, StringComparison.Ordinal);
        AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task HealthIsOkAndAnonymous()
    {
        using var anon = factory.CreateAnonymousClient();
        using var response = await anon.GetAsync(new Uri("/health", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).Str("status"));
        AssertSecurityHeaders(response);
    }

    [Theory]
    [InlineData("/api/session")]
    [InlineData("/api/auth/dev-users")]
    [InlineData("/api/apps/barebone-builder/intakes")]
    public async Task EveryResponseCarriesSecurityHeaders(string path)
    {
        using var anon = factory.CreateAnonymousClient();
        using var response = await anon.GetAsync(new Uri(path, UriKind.Relative));
        AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task ErrorsAreProblemJsonWithoutStackTraces()
    {
        using var anon = factory.CreateAnonymousClient();
        using (var unauthorized = await anon.GetAsync(new Uri(Paths.Builder + "/intakes", UriKind.Relative)))
        {
            await AssertSafeProblemAsync(unauthorized, 401);
        }

        using (var csrf = await anon.PostAsJsonAsync(new Uri("/api/auth/login", UriKind.Relative), new { userId = Users.Bob }))
        {
            await AssertSafeProblemAsync(csrf, 403);
        }

        using var mallory = await factory.LoginAsAsync(Users.Mallory);
        using (var forbidden = await mallory.GetAsync(Paths.Builder + "/intakes"))
        {
            await AssertSafeProblemAsync(forbidden, 403);
        }

        using var bob = await factory.LoginAsAsync(Users.Bob);
        using (var missingIntake = await bob.GetAsync(Paths.Builder + "/intakes/999999999"))
        {
            await AssertSafeProblemAsync(missingIntake, 404);
        }

        using (var noKey = await bob.SendAsync(HttpMethod.Post, Paths.Builder + "/intakes", Scenario.IntakeBody("hdr-test"), null))
        {
            await AssertSafeProblemAsync(noKey, 400);
        }

        using var malformed = await bob.SendAsync(HttpMethod.Post, Paths.Builder + "/intakes", null, PlatformClient.NewKey(), rawJson: "{\"title\": [unclosed");
        var text = await malformed.Content.ReadAsStringAsync();
        Assert.True((int)malformed.StatusCode is >= 400 and < 600, text);
        Assert.Equal("application/problem+json", malformed.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("   at ", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".cs:line", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorrelationIdIsEchoedOnlyWhenWellFormed()
    {
        using var anon = factory.CreateAnonymousClient();
        using (var good = new HttpRequestMessage(HttpMethod.Get, "/health"))
        {
            good.Headers.Add("X-Correlation-Id", "test-corr-12345678");
            using var response = await anon.SendAsync(good);
            Assert.Equal("test-corr-12345678", Header(response, "X-Correlation-Id"));
        }

        using var bad = new HttpRequestMessage(HttpMethod.Get, "/health");
        bad.Headers.TryAddWithoutValidation("X-Correlation-Id", "<script>alert(1)</script>");
        using var badResponse = await anon.SendAsync(bad);
        var echoed = Header(badResponse, "X-Correlation-Id");
        Assert.NotEqual("<script>alert(1)</script>", echoed);
        Assert.Matches("^[A-Za-z0-9-]{8,64}$", echoed);
    }
}
