using System.Text.Json;
using Vapolia.Analytics.AnalyticsCli;
using Vapolia.Analytics.Clients;

const string Usage = """
    vana, Vapolia analytics

      vana auth login [<portal url>]     Log in in the browser. Without a URL, reuses the last one.
      vana auth status                   Shows the portal, the session end, and the projects.
      vana auth logout                   Revokes the session and deletes it.

      vana projects                      Lists your projects.
      vana views <project>               Lists a project's views and their description.
      vana view <project> <name> [--limit N] [--offset N] [--since yyyy-MM-dd] [--filter column=value]...
                                         Prints a view's rows, as JSON.
      vana instructions <project> [--set <file>]
                                         Prints the project's instructions, or replaces them (managers).
    """;

var clock = TimeProvider.System;
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

try
{
    return await Run(args);
}
catch (CliException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
catch (AnalyticsException e)
{
    Console.Error.WriteLine($"{e.Status}: {e.Message}");
    return 1;
}
catch (HttpRequestException e)
{
    Console.Error.WriteLine($"Network error: {e.Message}");
    return 1;
}

async Task<int> Run(string[] a)
{
    switch (a)
    {
        case ["auth", "login", ..]:
        {
            var url = a.Length > 2 ? a[2] : Session.LastPortalUrl() ?? throw new CliException("Give the portal URL: vana auth login <url>");
            var session = await Login.Run(http, url, clock, cancel.Token);
            session.Save();
            Console.WriteLine($"Logged in to {session.PortalUrl}. The session ends on {session.SessionExpiresAt.LocalDateTime:yyyy-MM-dd}.");
            return 0;
        }
        case ["auth", "status"]:
        {
            var session = Session.Load() ?? throw new CliException("Not logged in. Run: vana auth login <url>");
            Console.WriteLine($"Portal: {session.PortalUrl}");
            Console.WriteLine($"Session ends: {session.SessionExpiresAt.LocalDateTime:yyyy-MM-dd HH:mm}");
            foreach (var p in await Client(session).Projects(await Token(session), cancel.Token))
                Console.WriteLine($"  {p.Id,-20} {p.Role,-8} {p.Name}{(p.Company is null ? "" : $" ({p.Company})")}");
            return 0;
        }
        case ["auth", "logout"]:
        {
            if (Session.Load() is { } session)
                await Login.Revoke(http, session, cancel.Token);
            Session.Delete();
            Console.WriteLine("Logged out.");
            return 0;
        }
        case ["projects"]:
        {
            var session = Current();
            var projects = await Client(session).Projects(await Token(session), cancel.Token);
            Console.WriteLine(JsonSerializer.Serialize(projects, CliJson.Default.ListProject));
            return 0;
        }
        case ["views", var project]:
        {
            var session = Current();
            Print(await Client(session).ListViews(await Token(session), project, cancel.Token));
            return 0;
        }
        case ["view", var project, var name, .. var options]:
        {
            var session = Current();
            var (limit, offset, since, filters) = ParseViewOptions(options);
            Print(await Client(session).GetView(await Token(session), project, name, limit, offset, since, filters, cancel.Token));
            return 0;
        }
        case ["instructions", var project]:
        {
            var session = Current();
            Console.WriteLine((await Client(session).GetInstructions(await Token(session), project, cancel.Token)).Text);
            return 0;
        }
        case ["instructions", var project, "--set", var file]:
        {
            var session = Current();
            await Client(session).SaveInstructions(await Token(session), project, await File.ReadAllTextAsync(file, cancel.Token), cancel.Token);
            Console.WriteLine("Saved.");
            return 0;
        }
        default:
            Console.Error.WriteLine(Usage);
            return a is [] or ["help"] or ["--help"] or ["-h"] ? 0 : 2;
    }
}

Session Current()
{
    var session = Session.Load() ?? throw new CliException("Not logged in. Run: vana auth login <url>");
    var left = session.SessionExpiresAt - clock.GetUtcNow();
    if (left > TimeSpan.Zero && left < Session.Warning)
        Console.Error.WriteLine($"The session ends on {session.SessionExpiresAt.LocalDateTime:yyyy-MM-dd HH:mm}. Run vana auth login to renew it.");
    return session;
}

Task<string> Token(Session session) => Login.AccessToken(http, session, clock, cancel.Token);

PublicClient Client(Session session) => new(http, new Uri(session.PortalUrl.TrimEnd('/') + "/"));

static void Print(string json)
{
    using var document = JsonDocument.Parse(json);
    Console.WriteLine(JsonSerializer.Serialize(document.RootElement, CliJson.Default.JsonElement));
}

static (int?, int?, DateOnly?, Dictionary<string, string[]>) ParseViewOptions(string[] options)
{
    int? limit = null, offset = null;
    DateOnly? since = null;
    var filters = new Dictionary<string, List<string>>();
    for (var i = 0; i < options.Length; i++)
    {
        var value = i + 1 < options.Length ? options[i + 1] : throw new CliException($"{options[i]} needs a value.");
        switch (options[i])
        {
            case "--limit" when int.TryParse(value, out var l): limit = l; break;
            case "--offset" when int.TryParse(value, out var o): offset = o; break;
            case "--since" when DateOnly.TryParseExact(value, "yyyy-MM-dd", out var s): since = s; break;
            case "--filter" when value.IndexOf('=') is > 0 and var eq:
                var column = value[..eq];
                if (!filters.TryGetValue(column, out var list))
                    filters[column] = list = [];
                list.Add(value[(eq + 1)..]);
                break;
            default: throw new CliException($"Unexpected option: {options[i]} {value}");
        }
        i++;
    }
    return (limit, offset, since, filters.ToDictionary(f => f.Key, f => f.Value.ToArray()));
}
