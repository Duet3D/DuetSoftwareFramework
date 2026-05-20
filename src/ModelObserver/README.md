# ModelObserver

`ModelObserver` is the command-line tool for watching DSF object-model updates as they are pushed from `DuetControlServer`. It is the quickest way to see what subscribers receive without opening a browser or writing a custom plugin.

## How It Works

[Program.cs](Program.cs) exposes optional `--filter` expressions plus a `--confirm` mode. [Commands.cs](Commands.cs) opens a subscribe-mode IPC connection to DCS and prints the JSON updates that match the requested filters.

Because the tool is using the same subscription path as higher-level consumers, it is useful both for debugging and for learning how model deltas are shaped.

## Interfaces

| Interface | Details |
|---|---|
| DCS IPC | Subscribe-mode connection over the UNIX socket. |
| Other DSF services | None directly, but it observes the same DCS model that DWS exposes to browser clients. |
| RepRapFirmware | Indirect only. Most interesting updates originate in firmware and are merged into the DCS object model before they reach subscribers. |

## Typical Uses

- verify which subtree changes after a firmware action;
- test filter expressions before using them in another client;
- inspect how often a field is updated and whether the values are DSF- or RRF-owned.

## Build And Verify

```sh
dotnet build ModelObserver.csproj
dotnet run --project ModelObserver.csproj -- --help
```

## Related Docs

- [../DuetControlServer/README.md](../DuetControlServer/README.md)
- [../DuetWebServer/README.md](../DuetWebServer/README.md)
- [../../docs/devel/OBJECT_MODEL.md](../../docs/devel/OBJECT_MODEL.md)
- [../../docs/devel/IPC_PROTOCOL.md](../../docs/devel/IPC_PROTOCOL.md)
