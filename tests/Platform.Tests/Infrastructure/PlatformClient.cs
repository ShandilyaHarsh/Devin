using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Platform.Tests.Infrastructure;

/// <summary>Authenticated test client: sends X-CSRF-Token on every request and an Idempotency-Key on mutations.</summary>
public sealed class PlatformClient(HttpClient http, string userId) : IDisposable
{
    public HttpClient Http { get; } = http;

    public string UserId { get; } = userId;

    public string CsrfToken { get; private set; } = string.Empty;

    public static string NewKey() => "test-" + Guid.NewGuid().ToString("N");

    public static async Task<string> FetchCsrfTokenAsync(HttpClient http)
    {
        using var response = await http.GetAsync(new Uri("/api/session", UriKind.Relative));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("csrfToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token), "GET /api/session must return a csrfToken");
        return token!;
    }

    public async Task RefreshCsrfAsync() => CsrfToken = await FetchCsrfTokenAsync(Http);

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path, null, null);

    public Task<HttpResponseMessage> PostAsync(string path, object? body, string? idempotencyKey = null) =>
        SendAsync(HttpMethod.Post, path, body, idempotencyKey ?? NewKey());

    public Task<HttpResponseMessage> PutAsync(string path, object? body, string? idempotencyKey = null) =>
        SendAsync(HttpMethod.Put, path, body, idempotencyKey ?? NewKey());

    /// <summary>Full control: pass includeCsrf=false / idempotencyKey=null to omit headers; rawJson to send exact bytes.</summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        string? idempotencyKey,
        bool includeCsrf = true,
        string? csrfOverride = null,
        string? rawJson = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (includeCsrf && method != HttpMethod.Get)
        {
            request.Headers.Add("X-CSRF-Token", csrfOverride ?? CsrfToken);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (rawJson is not null)
        {
            request.Content = new StringContent(rawJson, Encoding.UTF8, "application/json");
        }
        else if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        else if (method != HttpMethod.Get)
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        return await Http.SendAsync(request);
    }

    public async Task<JsonElement> GetJsonAsync(string path)
    {
        using var response = await GetAsync(path);
        return await Json.ReadOkAsync(response, $"GET {path} as {UserId}");
    }

    public async Task<JsonElement> PostJsonAsync(string path, object? body, string? idempotencyKey = null)
    {
        using var response = await PostAsync(path, body, idempotencyKey);
        return await Json.ReadOkAsync(response, $"POST {path} as {UserId}");
    }

    public async Task<JsonElement> PutJsonAsync(string path, object? body, string? idempotencyKey = null)
    {
        using var response = await PutAsync(path, body, idempotencyKey);
        return await Json.ReadOkAsync(response, $"PUT {path} as {UserId}");
    }

    public void Dispose() => Http.Dispose();
}

public static class Json
{
    public static async Task<JsonElement> ReadOkAsync(HttpResponseMessage response, string what)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{what} expected 2xx but got {(int)response.StatusCode}: {text}");
        return Parse(text);
    }

    public static JsonElement Parse(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    public static string? Str(this JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? (value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText())
            : null;

    /// <summary>Id as it should appear in a URL (works for numeric and string ids).</summary>
    public static string Id(this JsonElement element) =>
        element.Str("id") ?? throw new InvalidOperationException("element has no id: " + element.GetRawText());
}
