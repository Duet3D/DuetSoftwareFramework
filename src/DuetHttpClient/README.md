# DuetHttpClient

Official remote HTTP API client for Duet3D boards by Duet3D.

`DuetHttpClient` is the standalone HTTP client library for talking to Duet controllers over their HTTP API. It is separate from `DuetAPIClient`: this project targets network HTTP endpoints, whereas `DuetAPIClient` targets the local DSF UNIX socket.

## What This Project Owns

| Area | Purpose |
|---|---|
| [DuetHttpSession.cs](DuetHttpSession.cs) | Main entry point for creating and using a remote session. |
| [Connector/](Connector) | Transport-specific implementations for poll-style and REST-style communication. |
| [DuetHttpOptions.cs](DuetHttpOptions.cs) | Connection options such as credentials and observation behavior. |
| [Utility/](Utility) | File-list and JSON helper types returned by HTTP operations. |
| [Exceptions/](Exceptions) | Remote-login and compatibility errors surfaced to callers. |

## How It Works

The main factory is `DuetHttpSession.ConnectAsync(...)` in [DuetHttpSession.cs](DuetHttpSession.cs). It tries to establish a `PollConnector` first and falls back to a `RestConnector` if that fails. Once connected, callers can:

- send codes;
- observe the remote object model;
- upload, download, move, and delete files;
- fetch file lists and parsed G-code metadata.

The project reuses the `DuetAPI.ObjectModel` types so HTTP clients and IPC clients can work with the same machine-state schema.

## Interfaces With Other DSF Projects

| Consumer | Interface |
|---|---|
| [../DuetAPI/README.md](../DuetAPI/README.md) | Supplies the shared object-model types returned by the HTTP client. |
| [../UnitTests/README.md](../UnitTests/README.md) | Contains regression tests for HTTP session behavior. |
| External tooling | Uploaders, remote utilities, and non-SBC integrations can use this library without hosting DSF locally. |

Within the DSF solution, this project is mostly independent. The core SBC services do not use it for their internal control path.

## Relationship To RepRapFirmware

This project can talk to RepRapFirmware directly when a board is operating in standalone mode and exposing the HTTP API itself. In SBC mode, it can also talk to `DuetWebServer` through the RRF-compatible HTTP endpoints implemented by [../DuetWebServer/Controllers/RepRapFirmwareController.cs](../DuetWebServer/Controllers/RepRapFirmwareController.cs).

## Build And Verify

```sh
dotnet build DuetHttpClient.csproj
dotnet test ../UnitTests/UnitTests.csproj --filter HttpClient
```

## Related Docs

- [../DuetWebServer/README.md](../DuetWebServer/README.md)
- [../../docs/devel/HTTP_API.md](../../docs/devel/HTTP_API.md)
