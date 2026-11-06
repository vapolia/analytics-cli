# analytics-cli

`vana`, the command line client of Vapolia analytics. It reads an app's audience figures with the rights of the person logged in: no
key to copy, no credential stored on a server.

## Summary

1. `vana auth login <portal url>`, then `vana projects`, `vana views`, `vana view`.
2. The CLI calls the analytics portal, which checks the membership and hands back a 5-minute read token for one project, then reads
   the views from the collector's read server with that token only.
3. The CLI holds no credential of its own. The portal URL is given at login and is not in this repository.

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

- The login opens the browser on the portal, with a loopback redirect (RFC 8252) and PKCE.
- The session lasts 30 days from login and is never extended. From 5 days before its end, every command says when it ends.
- The session file is in `%APPDATA%/vana` (Windows) or `~/.config/vana`, readable by its owner only. `VANA_CONFIG_DIR` moves it.

A published GitHub release tagged `vX.Y.Z` gets one native binary per platform (Windows x64, macOS arm64, Linux x64), at version `X.Y.Z`. To build one:

```powershell
dotnet publish src/Vapolia.Analytics.AnalyticsCli -c Release -r win-x64
```

## Development

```powershell
dotnet build
dotnet run --project tests/Vapolia.Analytics.AnalyticsCli.Tests
```
