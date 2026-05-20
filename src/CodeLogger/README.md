# CodeLogger

`CodeLogger` is a diagnostic client for observing the lifecycle of G/M/T-codes inside `DuetControlServer`. It connects to the DCS interception interface and prints the codes that pass through selected stages of the pipeline.

## How It Works

[Program.cs](Program.cs) lets you choose:

- one or more interception stages via `--type` (`Pre`, `Post`, `Executed`);
- optional channel filters via `--channel`;
- optional code filters via `--filters`;
- whether priority codes should be intercepted.

The implementation in [Commands.cs](Commands.cs) opens an intercept-mode connection through [../DuetAPIClient/README.md](../DuetAPIClient/README.md). Once connected, it receives codes as DCS reaches the requested pipeline stage and writes them to stdout.

## Interfaces

| Interface | Details |
|---|---|
| DCS IPC | Intercept-mode connection over the UNIX socket. |
| Other DSF services | None directly. This tool observes DCS, which is already coordinating with DWS, plugins, and tools. |
| RepRapFirmware | Indirect only. The `Post` and `Executed` stages can reflect codes that are about to reach or have already returned from firmware. |

## Why It Matters

`CodeLogger` is useful when you need to answer questions like:

- did a plugin resolve this code before it reached firmware;
- which channel submitted the code;
- did DSF rewrite, defer, or reject a code before the firmware stage;
- what order are intercepted codes appearing in under load.

## Build And Verify

```sh
dotnet build CodeLogger.csproj
dotnet run --project CodeLogger.csproj -- --help
```

## Related Docs

- [../DuetControlServer/README.md](../DuetControlServer/README.md)
- [../../docs/devel/CODE_PIPELINE.md](../../docs/devel/CODE_PIPELINE.md)
- [../../docs/devel/IPC_PROTOCOL.md](../../docs/devel/IPC_PROTOCOL.md)
