using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;

namespace AdbSync.Web.Services;

public sealed class ApiException(HttpStatusCode status, string code, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public string Code { get; } = code;

    public static async Task<ApiException> FromAsync(HttpResponseMessage response)
    {
        var code = "http_" + (int)response.StatusCode;
        var message = response.ReasonPhrase ?? "Request failed.";
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.TryGetProperty("code", out var c) && c.GetString() is { } codeText)
                code = codeText;
            var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            var detail = root.TryGetProperty("detail", out var d) ? d.GetString() : null;
            message = (title, detail) switch
            {
                ({ } a, { } b) when a != b => $"{a} {b}",
                ({ } a, _) => a,
                (_, { } b) => b,
                _ => message,
            };
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                var fields = errors.EnumerateObject()
                    .SelectMany(p => p.Value.EnumerateArray().Select(m => $"{p.Name.TrimStart('/')}: {m.GetString()}"));
                message += " " + string.Join(" ", fields);
            }
        }
        catch (JsonException)
        {
        }
        return new ApiException(response.StatusCode, code, message);
    }
}

public static class ApiErrors
{
    /// <summary>A 401: the client has already signed out and gone to the sign-in page, so there's nothing to report.</summary>
    public static bool IsSignedOut(this Exception ex) => ex is ApiException { Status: HttpStatusCode.Unauthorized };
}

/// <summary>Calls /api/v1 with the session's token. A 401 signs the browser out and goes back to the sign-in page.</summary>
public sealed class ApiClient(HttpClient http, Session session, NavigationManager navigation)
{
    private static readonly MediaTypeHeaderValue MergePatch = new("application/merge-patch+json");

    public async Task<T> GetAsync<T>(string path, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, null, ct);
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }

    public async Task<string> GetTextAsync(string path, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, null, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<byte[]> GetBytesAsync(string path, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, null, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<T> PostAsync<T>(string path, object? body = null, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body, null, ct);
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }

    public async Task PostAsync(string path, object? body = null, CancellationToken ct = default)
    {
        using var _ = await SendAsync(HttpMethod.Post, path, body, null, ct);
    }

    public async Task<T> PatchAsync<T>(string path, object body, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Patch, path, body, MergePatch, ct);
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        using var _ = await SendAsync(HttpMethod.Delete, path, null, null, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, MediaTypeHeaderValue? mediaType, CancellationToken ct)
    {
        await session.LoadAsync();
        using var request = new HttpRequestMessage(method, "api/v1/" + path.TrimStart('/'));
        if (session.Token is { } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), mediaType);

        var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
            return response;

        var error = await ApiException.FromAsync(response);
        response.Dispose();
        if (error.Status == HttpStatusCode.Unauthorized && session.SignedIn)
        {
            await session.SignOutAsync();
            navigation.NavigateTo("signin");
        }
        throw error;
    }
}
