# analytics-cli

`vana` reads the figures of Vapolia analytics from the command line.

## Summary

1. Log in with `vana auth login <portal url>`. The portal URL comes from your administrator.
2. List your projects and their views with `vana projects` and `vana views <project>`.
3. Read a view with `vana view <project> <name>`.

## CLI

```text
vana auth login [<portal url>]     Log in in the browser. Without a URL, reuses the last one.
vana auth status                   Shows the portal, the session end, and the projects.
vana auth logout                   Revokes the session and deletes it.
vana projects                      Lists your projects.
vana views <project>               Lists a project's views and their description.
vana view <project> <name> [--limit N] [--offset N] [--since yyyy-MM-dd] [--filter column=value]...
vana instructions <project> [--set <file>]
```

- A session lasts 30 days. From 5 days before its end, every command says when it ends.
- The session file is in `%APPDATA%/vana` (Windows) or `~/.config/vana`. Set `VANA_CONFIG_DIR` to store it elsewhere.

Each GitHub release has a binary for Windows x64, macOS arm64 and Linux x64. To build one:

```powershell
dotnet publish src/cli -c Release -r win-x64
```

## Development

```powershell
dotnet build
dotnet run --project src/Vapolia.Analytics.AnalyticsCli.Tests
```
