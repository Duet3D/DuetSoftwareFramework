# CodeStream

`CodeStream` is the buffered counterpart to `CodeConsole`. It is intended for scenarios where you want to stream many codes through DCS without waiting for each one to finish before sending the next.

## How It Works

[Program.cs](Program.cs) exposes a single `--buffer-size` option that controls how many codes may be in flight at once. [Commands.cs](Commands.cs) opens a code-stream IPC connection to DCS and keeps feeding the stream until the buffer limit is reached, then prints replies as DCS resolves them.

That makes it useful for throughput testing and for tools that need more overlap than the request/response command mode offers.

## Interfaces

| Interface | Details |
|---|---|
| DCS IPC | Code-stream connection over the UNIX socket. |
| Other DSF services | None directly. It relies on DCS to route the work into the normal code pipeline. |
| RepRapFirmware | Indirect only. Firmware-bound codes still go through DCS before they reach RRF. |

## When To Use It

- bulk code submission where per-code round-trip latency is not desirable;
- stress-testing the code pipeline or channel handling;
- comparing buffered versus strictly synchronous code execution behavior.

## Build And Verify

```sh
dotnet build CodeStream.csproj
dotnet run --project CodeStream.csproj -- --help
```

## Related Docs

- [../DuetControlServer/README.md](../DuetControlServer/README.md)
- [../../docs/devel/CODE_PIPELINE.md](../../docs/devel/CODE_PIPELINE.md)
- [../../docs/devel/IPC_PROTOCOL.md](../../docs/devel/IPC_PROTOCOL.md)
