# DuetAPIClient

Official client API library for Duet Software Framework (DSF) by Duet3D.

This package provides classes to connect to Duet Control Server over its UNIX socket in SBC mode, allowing applications to query and subscribe to the object model, send G/M/T-codes and interact with the framework.

## Documentation

See the [DSF documentation](https://duet3d.github.io/DuetSoftwareFramework/index.html) for details.

## License

Licensed under LGPL-3.0-or-later.

## What This Project Owns

| Area | Purpose |
|---|---|
| [BaseConnection.cs](BaseConnection.cs) | Shared socket lifecycle, handshake, and transport behavior. |
| [Commands/](Commands) | Command/request-response clients for normal DCS operations. |
| [CodeInterception/](CodeInterception) | Clients for pre/post/executed code interception. |
| [ModelSubscription/](ModelSubscription) | Clients that subscribe to object-model deltas. |
| [CodeStream/](CodeStream) | Buffered asynchronous code streaming connections. |
| [HttpEndpoints/](HttpEndpoints) | Registration and communication helpers for custom HTTP endpoints. |

## How It Works

Each connection type starts from [BaseConnection.cs](BaseConnection.cs), which opens the UNIX socket and performs the IPC initialization handshake defined in [../../docs/devel/IPC_PROTOCOL.md](../../docs/devel/IPC_PROTOCOL.md). Derived classes then expose behavior tailored to one mode:

- `CommandConnection` for request/response commands;
- `InterceptConnection` for observing or resolving codes in the DCS pipeline;
- `SubscribeConnection` for object-model subscriptions;
- `CodeStreamConnection` for buffered G/M/T-code streaming;
- `HttpEndpointConnection` for third-party HTTP endpoint registration.

This is the library that most DSF tools and plugins should build against instead of manually implementing the socket protocol.

## Interfaces With Other DSF Projects

| Consumer | Interface |
|---|---|
| [../DuetWebServer/README.md](../DuetWebServer/README.md) | Uses command and subscription connections to bridge browsers to DCS. |
| [../DuetPluginService/README.md](../DuetPluginService/README.md) | Uses internal connections to coordinate plugin lifecycle actions. |
| CLI tools | `CodeConsole`, `CodeLogger`, `CodeStream`, `CustomHttpEndpoint`, `ModelObserver`, and `PluginManager` are thin front ends over this library. |
| Third-party plugins | Use it to send codes, subscribe to model changes, register HTTP endpoints, and manage plugin state. |

## Relationship To RepRapFirmware

`DuetAPIClient` never talks to RepRapFirmware directly. Every request goes to DCS over `/var/run/dsf/dcs.sock`, and DCS decides whether the work is handled internally or forwarded over SPI/USB to firmware.

## Runtime Contract

- Default socket path: `/var/run/dsf/dcs.sock`
- Transport: UNIX domain socket
- Protocol: DSF IPC handshake plus JSON command payloads
- Primary dependency: [../DuetAPI/README.md](../DuetAPI/README.md)

## Build And Verify

```sh
dotnet build DuetAPIClient.csproj
dotnet run --project ../CodeConsole/CodeConsole.csproj -- --help
```

The second command is a quick smoke test that a consumer of the library still builds and starts correctly.

## Related Docs

- [../../docs/devel/IPC_PROTOCOL.md](../../docs/devel/IPC_PROTOCOL.md)
- [../DuetAPI/README.md](../DuetAPI/README.md)
