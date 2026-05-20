# DuetAPI

`DuetAPI` is the public contract assembly for Duet Software Framework. It defines the types that every other DSF component uses to talk about machine state, commands, permissions, connection setup, and shared exceptions.

## What This Project Owns

| Area | Purpose |
|---|---|
| [ObjectModel/](ObjectModel) | Strongly typed representation of the printer state tree that DSF exposes to clients. |
| [Commands/](Commands) | Request and response types sent over the DCS IPC protocol. |
| [Connection/](Connection) | Connection modes, defaults, and startup metadata shared by DCS clients. |
| [Utility/](Utility) | Permissions, JSON helpers, path helpers, and other API-facing support types. |
| [Exceptions/](Exceptions) | Errors that callers can catch without depending on server internals. |

The two files most other projects care about first are [ObjectModel/ObjectModel.cs](ObjectModel/ObjectModel.cs) and [Commands/Command.cs](Commands/Command.cs).

## How It Works

The classes in this project are the schema that ties the DSF stack together:

- `DuetControlServer` instantiates and updates these types while it mirrors RepRapFirmware state and handles IPC commands.
- `DuetWebServer`, `DuetPluginService`, bundled tools, and third-party plugins deserialize the same types so they can work against a stable contract instead of server-private DTOs.
- The project is paired with [../DuetAPI.SourceGenerators/README.md](../DuetAPI.SourceGenerators/README.md), which emits part of the serialization and object-model plumbing at compile time.

In practice this means API changes here propagate everywhere. New object-model properties, command payloads, and permission flags usually require coordinated changes in DCS, DWS, plugins, tests, and sometimes RepRapFirmware.

## Interfaces With Other DSF Projects

| Consumer | Interface |
|---|---|
| [../DuetAPIClient/README.md](../DuetAPIClient/README.md) | Wraps these contracts in higher-level IPC connection classes. |
| [../DuetControlServer/README.md](../DuetControlServer/README.md) | Owns the authoritative runtime instances and serializes them over IPC. |
| [../DuetWebServer/README.md](../DuetWebServer/README.md) | Uses the object model and command contracts for HTTP and WebSocket endpoints. |
| [../DuetPluginService/README.md](../DuetPluginService/README.md) | Uses command and permission types for plugin lifecycle management. |
| Tools and plugins | Build directly against `DuetAPI` or consume it transitively through `DuetAPIClient`. |

## Relationship To RepRapFirmware

`DuetAPI` does not talk to RepRapFirmware directly. The dependency is contractual rather than physical:

- large parts of [CodeChannel.cs](CodeChannel.cs) and the object-model naming mirror firmware-side concepts;
- DCS maps SPI packets and object-model JSON subtrees from RRF onto these C# types;
- permission and capability flags exposed on the SBC side often correspond to features that originate in firmware.

If the firmware-side model changes, this project is usually one of the first places that needs updating.

## Build And Verify

```sh
dotnet build DuetAPI.csproj
dotnet test ../UnitTests/UnitTests.csproj
```

When changing the object model or command contracts, regenerate and review the documentation pipeline as well because `DocGen` and `DocFX` consume this assembly.

## Related Docs

- [../../docs/devel/OBJECT_MODEL.md](../../docs/devel/OBJECT_MODEL.md)
- [../../docs/devel/IPC_PROTOCOL.md](../../docs/devel/IPC_PROTOCOL.md)
- [../DuetAPI.SourceGenerators/README.md](../DuetAPI.SourceGenerators/README.md)
