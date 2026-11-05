using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace Vana.Cli;

/// <summary>Logs in through the portal in the browser, with a loopback redirect (RFC 8252) and PKCE.</summary>
public static class Login
{
    public static async Task<Session> Run(HttpClient http, string portalUrl, TimeProvider clock, CancellationToken cancel)
    {
        portalUrl = portalUrl.TrimEnd('/');
        var metadata = await http.GetFromJsonAsync(portalUrl + "/.well-known/oauth-authorization-server", CliJson.Default.ServerMetadata, cancel)
                       ?? throw new CliException("The portal published no OAuth metadata.");
        if (metadata.RegistrationEndpoint is null)
            throw new CliException("The portal does not register clients.");

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var redirectUri = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/callback";

        var registration = await http.PostAsJsonAsync(metadata.RegistrationEndpoint,
            new RegistrationRequest("vana", [redirectUri], "none"), CliJson.Default.RegistrationRequest, cancel);
        if (!registration.IsSuccessStatusCode)
            throw new CliException($"Client registration refused: {await registration.Content.ReadAsStringAsync(cancel)}");
        var clientId = (await registration.Content.ReadFromJsonAsync(CliJson.Default.RegistrationResponse, cancel))!.ClientId;

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var resource = portalUrl + "/api";
        var url = metadata.AuthorizationEndpoint + "?" + string.Join('&', new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = clientId, ["redirect_uri"] = redirectUri,
            ["scope"] = "projects projects:write offline_access", ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
            ["state"] = state, ["resource"] = resource,
        }.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

        Console.Error.WriteLine("Opening the browser to log in. If it does not open, visit:");
        Console.Error.WriteLine(url);
        OpenBrowser(url);

        var code = await WaitForCode(listener, state, cancel);
        var now = clock.GetUtcNow();
        var tokens = await Token(http, metadata.TokenEndpoint, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier, ["resource"] = resource,
        }, cancel);

        return new Session
        {
            PortalUrl = portalUrl, ClientId = clientId, RedirectUri = redirectUri, AccessToken = tokens.AccessToken!,
            RefreshToken = tokens.RefreshToken, AccessExpiresAt = now.AddSeconds(tokens.ExpiresIn ?? 3600),
            SessionExpiresAt = now + Session.Lifetime,
        };
    }

    /// <summary>A fresh access token, refreshed when the current one is about to expire.</summary>
    public static async Task<string> AccessToken(HttpClient http, Session session, TimeProvider clock, CancellationToken cancel)
    {
        var now = clock.GetUtcNow();
        if (now >= session.SessionExpiresAt)
            throw new CliException("The session has expired. Run: vana auth login");
        if (now < session.AccessExpiresAt.AddMinutes(-1))
            return session.AccessToken;
        if (session.RefreshToken is null)
            throw new CliException("The session cannot be renewed. Run: vana auth login");

        var tokens = await Token(http, session.PortalUrl + "/connect/token", new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["client_id"] = session.ClientId, ["refresh_token"] = session.RefreshToken,
        }, cancel);
        session.AccessToken = tokens.AccessToken!;
        session.RefreshToken = tokens.RefreshToken ?? session.RefreshToken;
        session.AccessExpiresAt = now.AddSeconds(tokens.ExpiresIn ?? 3600);
        session.Save();
        return session.AccessToken;
    }

    public static async Task Revoke(HttpClient http, Session session, CancellationToken cancel)
    {
        if (session.RefreshToken is null)
            return;
        using var _ = await http.PostAsync(session.PortalUrl + "/connect/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = session.RefreshToken, ["token_type_hint"] = "refresh_token", ["client_id"] = session.ClientId,
        }), cancel);
    }

    static async Task<TokenResponse> Token(HttpClient http, string endpoint, Dictionary<string, string> form, CancellationToken cancel)
    {
        using var response = await http.PostAsync(endpoint, new FormUrlEncodedContent(form), cancel);
        var tokens = await response.Content.ReadFromJsonAsync(CliJson.Default.TokenResponse, cancel);
        if (!response.IsSuccessStatusCode || tokens?.AccessToken is null)
            throw new CliException($"The portal refused: {tokens?.ErrorDescription ?? tokens?.Error ?? response.ReasonPhrase}. Run: vana auth login");
        return tokens;
    }

    /// <summary>Answers the browser's redirect and returns the code. Anything else on the port is ignored.</summary>
    static async Task<string> WaitForCode(TcpListener listener, string state, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(timeout.Token) ?? "";
            var target = requestLine.Split(' ') is [_, var t, ..] ? t : "";
            if (!target.StartsWith("/callback", StringComparison.Ordinal))
            {
                await Respond(stream, 404, "Not found.");
                continue;
            }

            var query = HttpUtility.ParseQueryString(target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "");
            if (query["state"] != state)
            {
                await Respond(stream, 400, "Unexpected state.");
                continue;
            }
            if (query["error"] is { } error)
            {
                await Respond(stream, 400, "Login refused. You can close this tab.");
                throw new CliException($"Login refused: {query["error_description"] ?? error}");
            }

            await Respond(stream, 200, "Logged in. You can close this tab and go back to the terminal.");
            return query["code"] ?? throw new CliException("The portal returned no code.");
        }
    }

    static async Task Respond(Stream stream, int status, string message)
    {
        var body = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><title>vana</title><p style=\"font:16px system-ui\">{WebUtility.HtmlEncode(message)}</p>");
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(body);
    }

    static void OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", url);
            else
                Process.Start("xdg-open", url);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The URL is printed: the user opens it by hand.
        }
    }

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class CliException(string message) : Exception(message);
