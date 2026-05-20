# DuetSharedLibrary

`DuetSharedLibrary` contains internal helpers shared across DSF executables. It exists so the core daemons do not each have to reimplement the same logging, path, process, socket, and version-handling code.

## What This Project Owns

| File or area | Purpose |
|---|---|
| [CommonLogFormatter.cs](CommonLogFormatter.cs) | Shared console log formatting. |
| [LogLevelHelper.cs](LogLevelHelper.cs) and [LogLevelJsonConverter.cs](LogLevelJsonConverter.cs) | Common parsing and serialization of log levels. |
| [VersionHelper.cs](VersionHelper.cs) | Shared product/version reporting. |
| [ProcessHelpers.cs](ProcessHelpers.cs) | Process-launch helpers used by service code. |
| [SocketExtensions.cs](SocketExtensions.cs) | Socket utility extensions used by IPC-facing code. |
| [PathExtensions.cs](PathExtensions.cs) and [FileExtensions.cs](FileExtensions.cs) | Common filesystem helpers. |
| [InternalCommandConnection.cs](InternalCommandConnection.cs) | Internal convenience wrapper around DSF command connections. |
| [Interop/](Interop) | Linux-specific interop support. |

## How It Works

This project deliberately stays small and practical. It does not define public DSF contracts; instead it packages the internal helpers that are useful in multiple executables. Typical examples are:

- common log-level parsing between command-line tools and daemons;
- helpers for launching and supervising child processes;
- extensions that keep UNIX-socket and file-path handling consistent.

If a type needs to be part of the public plugin/client surface, it usually belongs in [../DuetAPI/README.md](../DuetAPI/README.md) or [../DuetAPIClient/README.md](../DuetAPIClient/README.md) instead.

## Interfaces With Other DSF Projects

| Consumer | Interface |
|---|---|
| [../DuetControlServer/README.md](../DuetControlServer/README.md) | Uses helpers for logging, versioning, sockets, and process management. |
| [../DuetWebServer/README.md](../DuetWebServer/README.md) | Shares log, version, and utility code. |
| [../DuetPluginService/README.md](../DuetPluginService/README.md) | Uses helpers around command connections and process execution. |
| [../DuetPiManagementPlugin/README.md](../DuetPiManagementPlugin/README.md) | Reuses DSF-side shared utilities where appropriate. |

## Relationship To RepRapFirmware

There is no direct interface to RepRapFirmware. Any relationship is incidental and mediated through the DSF services that consume these helpers.

## Build And Verify

```sh
dotnet build DuetSharedLibrary.csproj
dotnet build ../DuetControlServer/DuetControlServer.csproj
```

The second build is the useful check because it confirms a major consumer still compiles against the shared helper surface.

## Related Docs

- [../DuetControlServer/README.md](../DuetControlServer/README.md)
- [../DuetWebServer/README.md](../DuetWebServer/README.md)
- [../DuetPluginService/README.md](../DuetPluginService/README.md)
