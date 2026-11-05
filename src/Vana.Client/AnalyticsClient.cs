using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vana.Client;

/// <summary>A refused call, with the status and the reason the server gave.</summary>
public sealed class AnalyticsException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed record Project(string Id, string Name, string? Company, string Role);

public sealed record Instructions(string Text, DateTime? UpdatedAt, string? UpdatedBy);

public sealed record InstructionsBody(string Text);

/// <summary>A short read token on one project's source, and where to use it.</summary>
public sealed record ReadAccess(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("read_url")] string ReadUrl,
    [property: JsonPropertyName("source")] string Source);

/// <summary>Reads a project's figures for one user: the portal for access, then the collector's read server for the rows.</summary>
/// <remarks>
/// The user's token goes to the portal only, which exchanges it for a 5-minute read token (RFC 8693). The read server only ever sees
/// that read token. Holds no credential of its own: the CLI passes the user's token at each call.
/// </remarks>
public sealed class AnalyticsClient(HttpClient http, Uri portal)
{
    public Task<List<Project>> Projects(string userToken, CancellationToken cancel = default) =>
        Send<List<Project>>(HttpMethod.Get, new Uri(portal, "api/projects"), userToken, null, cancel);

    public Task<Instructions> GetInstructions(string userToken, string project, CancellationToken cancel = default) =>
        Send<Instructions>(HttpMethod.Get, new Uri(portal, $"api/projects/{Uri.EscapeDataString(project)}/instructions"), userToken, null, cancel);

    public Task<Instructions> SaveInstructions(string userToken, string project, string text, CancellationToken cancel = default) =>
        Send<Instructions>(HttpMethod.Put, new Uri(portal, $"api/projects/{Uri.EscapeDataString(project)}/instructions"), userToken,
            JsonContent.Create(new InstructionsBody(text), ClientJson.Default.InstructionsBody), cancel);

    /// <param name="tool">What the read is for, written to the project's audit log with <paramref name="view"/>.</param>
    public async Task<ReadAccess> Exchange(string userToken, string project, string tool, string? view, CancellationToken cancel = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["subject_token"] = userToken,
            ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
            ["audience"] = project,
            ["tool"] = tool,
        };
        if (view is not null)
            form["view"] = view;

        using var response = await http.PostAsync(new Uri(portal, "api/token-exchange"), new FormUrlEncodedContent(form), cancel);
        return await Read<ReadAccess>(response, cancel);
    }

    /// <summary>The project's views: name, description, columns. JSON as the read server returns it.</summary>
    public async Task<string> ListViews(string userToken, string project, CancellationToken cancel = default)
    {
        var access = await Exchange(userToken, project, "list_views", null, cancel);
        return await ReadServer(access, "views", cancel);
    }

    /// <summary>The rows of one view, most recent first. JSON as the read server returns it.</summary>
    /// <param name="filters">Column → accepted values, compared as text.</param>
    public async Task<string> GetView(string userToken, string project, string name, int? limit = null, int? offset = null,
        DateOnly? since = null, IReadOnlyDictionary<string, string[]>? filters = null, CancellationToken cancel = default)
    {
        var query = new List<string>();
        if (limit is { } l) query.Add($"limit={l}");
        if (offset is { } o) query.Add($"offset={o}");
        if (since is { } s) query.Add($"since={s:yyyy-MM-dd}");
        foreach (var (column, values) in filters ?? new Dictionary<string, string[]>())
            foreach (var value in values)
                query.Add($"{Uri.EscapeDataString(column)}={Uri.EscapeDataString(value)}");

        var access = await Exchange(userToken, project, "get_view", name, cancel);
        var path = "views/" + Uri.EscapeDataString(name) + (query.Count > 0 ? "?" + string.Join('&', query) : "");
        return await ReadServer(access, path, cancel);
    }

    async Task<string> ReadServer(ReadAccess access, string path, CancellationToken cancel)
    {
        var baseUrl = access.ReadUrl.EndsWith('/') ? access.ReadUrl : access.ReadUrl + "/";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.AccessToken);
        using var response = await http.SendAsync(request, cancel);
        var body = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode)
            throw new AnalyticsException((int)response.StatusCode, ErrorOf(body) ?? response.ReasonPhrase ?? "read server error");
        return body;
    }

    async Task<T> Send<T>(HttpMethod method, Uri uri, string userToken, HttpContent? content, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
        using var response = await http.SendAsync(request, cancel);
        return await Read<T>(response, cancel);
    }

    static async Task<T> Read<T>(HttpResponseMessage response, CancellationToken cancel)
    {
        var body = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode)
            throw new AnalyticsException((int)response.StatusCode, ErrorOf(body) ?? response.ReasonPhrase ?? "portal error");
        var info = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)ClientJson.Default.GetTypeInfo(typeof(T))!;
        return JsonSerializer.Deserialize(body, info) ?? throw new AnalyticsException(500, "empty response");
    }

    static string? ErrorOf(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            var description = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            return description ?? (root.TryGetProperty("error", out var e) ? e.GetString() : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(List<Project>))]
[JsonSerializable(typeof(Instructions))]
[JsonSerializable(typeof(InstructionsBody))]
[JsonSerializable(typeof(ReadAccess))]
internal sealed partial class ClientJson : JsonSerializerContext;
