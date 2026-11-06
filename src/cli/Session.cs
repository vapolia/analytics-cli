using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vapolia.Analytics.AnalyticsCli;

/// <summary>The local login: the portal URL, the registered client, and the user's tokens.</summary>
/// <remarks>The session ends <see cref="Lifetime"/> after login, whatever its use: the portal never extends a refresh token.</remarks>
public sealed class Session
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan Warning = TimeSpan.FromDays(5);

    public required string PortalUrl { get; set; }
    public required string ClientId { get; set; }
    public required string RedirectUri { get; set; }
    public required string AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTimeOffset AccessExpiresAt { get; set; }
    public DateTimeOffset SessionExpiresAt { get; set; }

    /// <summary>The URL of the last login, kept after logout so that <c>vana auth login</c> works without one.</summary>
    public static string? LastPortalUrl() => File.Exists(LastUrlPath) ? File.ReadAllText(LastUrlPath).Trim() : null;

    public static Session? Load()
    {
        if (!File.Exists(SessionPath))
            return null;
        return JsonSerializer.Deserialize(File.ReadAllText(SessionPath), CliJson.Default.Session);
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        WritePrivate(SessionPath, JsonSerializer.Serialize(this, CliJson.Default.Session));
        File.WriteAllText(LastUrlPath, PortalUrl);
    }

    public static void Delete()
    {
        if (File.Exists(SessionPath))
            File.Delete(SessionPath);
    }

    static void WritePrivate(string path, string content)
    {
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    static string Folder => Environment.GetEnvironmentVariable("VANA_CONFIG_DIR") is { Length: > 0 } dir
        ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vana");

    static string SessionPath => Path.Combine(Folder, "session.json");
    static string LastUrlPath => Path.Combine(Folder, "portal-url");
}

public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_in")] int? ExpiresIn,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription);

public sealed record ServerMetadata(
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("authorization_endpoint")] string AuthorizationEndpoint,
    [property: JsonPropertyName("token_endpoint")] string TokenEndpoint,
    [property: JsonPropertyName("registration_endpoint")] string? RegistrationEndpoint,
    [property: JsonPropertyName("revocation_endpoint")] string? RevocationEndpoint);

public sealed record RegistrationRequest(
    [property: JsonPropertyName("client_name")] string ClientName,
    [property: JsonPropertyName("redirect_uris")] string[] RedirectUris,
    [property: JsonPropertyName("token_endpoint_auth_method")] string TokenEndpointAuthMethod);

public sealed record RegistrationResponse([property: JsonPropertyName("client_id")] string ClientId);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Session))]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(ServerMetadata))]
[JsonSerializable(typeof(RegistrationRequest))]
[JsonSerializable(typeof(RegistrationResponse))]
[JsonSerializable(typeof(List<Vapolia.Analytics.Clients.Project>))]
[JsonSerializable(typeof(Vapolia.Analytics.Clients.Instructions))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class CliJson : JsonSerializerContext;
