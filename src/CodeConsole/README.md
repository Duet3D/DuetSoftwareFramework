# CodeConsole

`CodeConsole` is the simplest interactive client for sending G/M/T-codes to `DuetControlServer`. It is primarily a development and administration tool for talking to the DSF command pipeline without going through the web UI.

## How It Works

[Program.cs](Program.cs) exposes two operating modes:

- the default interactive console mode, which reads codes from stdin and sends them one at a time;
- the `exec` mode, which submits the supplied code string and waits for the reply before exiting.

The implementation in [Commands.cs](Commands.cs) uses [../DuetAPIClient/README.md](../DuetAPIClient/README.md) to open a command-mode IPC connection to DCS over `/var/run/dsf/dcs.sock`.

## Interfaces

| Interface | Details |
|---|---|
| DCS IPC | Direct command connection over the UNIX socket. |
| Other DSF services | None directly; anything beyond DCS is reached through DCS. |
| RepRapFirmware | Indirect only. Codes that survive DSF processing are forwarded by DCS to firmware. |

## Typical Uses

- send one-off diagnostics such as `M122` or `M409`;
- test new DSF command-handling logic without a browser session;
- exercise the code pipeline from a shell script or SSH session.

## Build And Verify

```sh
dotnet build CodeConsole.csproj
dotnet run --project CodeConsole.csproj -- --help
```

With DCS running, a simple smoke test is:

```sh
dotnet run --project CodeConsole.csproj -- exec M115
```

## Related Docs

- [../DuetControlServer/README.md](../DuetControlServer/README.md)
- [../../docs/devel/CODE_PIPELINE.md](../../docs/devel/CODE_PIPELINE.md)
- [../../docs/devel/IPC_PROTOCOL.md](../../docs/devel/IPC_PROTOCOL.md)
