# DuetSoftwareFramework Developer Documentation

This directory contains developer documentation for **DuetSoftwareFramework (DSF)** — the .NET service stack that runs on a Linux SBC paired with a Duet 3 main board over SPI. DSF gives the printer a full Linux environment — proper file system, a real HTTP/WebSocket server, plugins — without giving up the deterministic real-time control offered by [RepRapFirmware](https://github.com/Duet3D/RepRapFirmware) on the MCU.

For build / packaging instructions, see [../DEVELOPER.md](../DEVELOPER.md). For end-user documentation see [../README.md](../README.md).

If you are also looking for the cross-repo picture (how DSF, RRF and Duet3Expansion fit together), see [../architecture/](../architecture).

## How to read these docs

Start with [ARCHITECTURE.md](ARCHITECTURE.md). From there, follow the link that matches what you are doing.

| Document | What it covers |
|---|---|
| [ARCHITECTURE.md](ARCHITECTURE.md) | Component map: DCS, DWS, plugin services, API client; processes, sockets, packages. |
| [DCS_INTERNALS.md](DCS_INTERNALS.md) | Inside DuetControlServer: services, hosted background tasks, dependency-injection layout, startup/shutdown. |
| [CODE_PIPELINE.md](CODE_PIPELINE.md) | The 6-stage code pipeline: Start → Pre → ProcessInternally → Post → Firmware → Executed. |
| [SPI_LINK.md](SPI_LINK.md) | The SPI link to RRF: framing, packet types, transfer state machine, file proxy, IAP. |
| [IPC_PROTOCOL.md](IPC_PROTOCOL.md) | The Unix-socket IPC protocol: connection modes, JSON commands, intercept / subscribe / code-stream / plugin services. |
| [HTTP_API.md](HTTP_API.md) | DuetWebServer: controllers, WebSocket, third-party endpoints, reverse-proxy mode, sessions. |
| [OBJECT_MODEL.md](OBJECT_MODEL.md) | The replicated machine state in `DuetAPI`: schema, observers, deltas, subscription delivery. |
| [PLUGINS.md](PLUGINS.md) | Plugin manifest, lifecycle (DuetPluginService), security profile, package layout. |
| [FILES.md](FILES.md) | Path resolution, virtual SD card, file parser, M28/M29 streamed-write, job processor. |
| [BUILD_VARIANTS.md](BUILD_VARIANTS.md) | Project layout, packages produced (`pkg/`), how DocFX docs are generated. |
| [MCODE_MIGRATION.md](MCODE_MIGRATION.md) | Porting `GCodes::HandleMcode` into DCS: inventory of ~190 M-codes, the porting contract, macro inventory, decisions. |
| [EVENTS_MIGRATION.md](EVENTS_MIGRATION.md) | Porting RepRapFirmware's event system into DCS, plus the new `controller_disconnect` / `controller_reconnect` link events. |

## Project READMEs

The documents above explain the system by subsystem. If you want the code-level view of an individual project, each solution project under [`src/`](../../src) now has its own README:

| Project | Focus |
|---|---|
| [DuetAPI](../../src/DuetAPI/README.md) | Public object-model, command, permission, and exception contracts. |
| [DuetAPI.SourceGenerators](../../src/DuetAPI.SourceGenerators/README.md) | Compile-time generator that fills in `DuetAPI` boilerplate. |
| [DuetAPIClient](../../src/DuetAPIClient/README.md) | IPC client library for command, intercept, subscribe, and endpoint connections. |
| [DuetSharedLibrary](../../src/DuetSharedLibrary/README.md) | Internal helpers shared by the DSF executables. |
| [DuetHttpClient](../../src/DuetHttpClient/README.md) | Standalone HTTP client for Duet boards and DWS compatibility endpoints. |
| [DuetControlServer](../../src/DuetControlServer/README.md) | Main DSF daemon, SPI link owner, object model owner, and IPC server. |
| [DuetWebServer](../../src/DuetWebServer/README.md) | HTTP/WebSocket bridge between browser clients and DCS. |
| [DuetPluginService](../../src/DuetPluginService/README.md) | Plugin lifecycle, security, and process supervision. |
| [DuetPiManagementPlugin](../../src/DuetPiManagementPlugin/README.md) | Bundled privileged plugin for SBC network and system-management M-codes. |
| [CodeConsole](../../src/CodeConsole/README.md) | Interactive and one-shot code sender. |
| [CodeLogger](../../src/CodeLogger/README.md) | Code-pipeline interception logger. |
| [CodeStream](../../src/CodeStream/README.md) | Buffered asynchronous code sender. |
| [CustomHttpEndpoint](../../src/CustomHttpEndpoint/README.md) | Example and utility for dynamic `/machine/...` endpoint registration. |
| [ModelObserver](../../src/ModelObserver/README.md) | Object-model subscription observer. |
| [PluginManager](../../src/PluginManager/README.md) | CLI front end for plugin lifecycle operations. |
| [DocGen](../../src/DocGen/README.md) | Object-model markdown generator. |
| [Documentation](../../src/Documentation/README.md) | DocFX project for the published docs site. |
| [UnitTests](../../src/UnitTests/README.md) | NUnit regression coverage across the DSF stack. |

## Companion repositories

DSF does not run alone. The other two repositories complete the system:

- **[RepRapFirmware](https://github.com/Duet3D/RepRapFirmware)** — runs on the Duet main board. DCS connects to it over SPI.
- **[Duet3Expansion](https://github.com/Duet3D/Duet3Expansion)** — firmware for CAN-attached tool / expansion boards. DSF has no direct contact with these — everything routes through RRF.
