# PluginManager

`PluginManager` is the CLI front end for DSF plugin lifecycle operations. It is useful on headless or SSH-only systems where you want to install, inspect, start, stop, or remove plugins without using DWC.

## How It Works

[Program.cs](Program.cs) defines the command-line surface and [Commands.cs](Commands.cs) turns those subcommands into DCS requests over a standard command-mode IPC connection. DCS then forwards the plugin operation to the appropriate `DuetPluginService` instance.

The currently exposed subcommands are:

- `list-data`
- `install <file>`
- `reload <id>`
- `start <id>`
- `set-data <id> <key> <value>`
- `stop <id>`
- `uninstall <id>`
- `is-installed <id>`
- `is-started <id>`

## Interfaces

| Interface | Details |
|---|---|
| DCS IPC | Standard command-mode connection over the UNIX socket. |
| `DuetPluginService` | Reached indirectly through DCS for install/start/stop/uninstall operations. |
| Plugin ZIPs and manifests | Input data for installs and metadata reloads. |
| RepRapFirmware | No direct interface. Plugin effects reach firmware only if the plugin itself later issues commands through DSF. |

## Why It Matters

This tool provides a low-friction way to validate plugin packaging and lifecycle behavior during development. It is also the simplest path for testing plugin-service changes without bringing the web UI into the loop.

## Build And Verify

```sh
dotnet build PluginManager.csproj
dotnet run --project PluginManager.csproj -- --help
```

## Related Docs

- [../DuetPluginService/README.md](../DuetPluginService/README.md)
- [../../docs/devel/PLUGINS.md](../../docs/devel/PLUGINS.md)
- [../../docs/devel/IPC_PROTOCOL.md](../../docs/devel/IPC_PROTOCOL.md)
