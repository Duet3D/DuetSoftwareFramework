# DuetControlServer

`DuetControlServer` (DCS) is the central daemon of Duet Software Framework. It is the only DSF process that talks directly to the Duet main board, and it owns the IPC socket, the live object model, the code pipeline, the virtual SD-card mapping, and the firmware-update path.

## At A Glance

| Aspect | Details |
|---|---|
| Entry point | [Program.cs](Program.cs) |
| Runtime type | Long-running console daemon, typically started by systemd |
| Main config | `/opt/dsf/conf/config.json` via [Settings.cs](Settings.cs) |
| Main IPC socket | `/var/run/dsf/dcs.sock` |
| Major subsystems | [Link/](Link), [IPC/](IPC), [Codes/](Codes), [Model/](Model), [Files/](Files), [Commands/](Commands), [Utility/](Utility) |

## What This Project Owns

- The SPI or USB transport to RepRapFirmware, including handshake, framing, packet parsing, retries, and firmware-update support.
- The authoritative DSF object model stored in [Model/ObjectModel.cs](Model/ObjectModel.cs).
- The UNIX-socket IPC server in [IPC/Server.cs](IPC/Server.cs), which all other DSF components use.
- The six-stage code pipeline implemented around [Codes/CodeProcessorService.cs](Codes/CodeProcessorService.cs) and [Codes/ChannelProcessor.cs](Codes/ChannelProcessor.cs).
- Virtual SD-card path translation, macro execution, job tracking, and file proxying in [Files/](Files).
- Command handlers that expose DSF services to clients in [Commands/](Commands).

## How It Works

At startup [Program.cs](Program.cs) builds a hosted service, loads [Settings.cs](Settings.cs), configures logging, opens the transport to the Duet board, and starts the background services that keep the system alive.

The control flow is split into a few major paths:

1. **Link path**. [Link/LinkService.cs](Link/LinkService.cs) and [Link/LinkInterface.cs](Link/LinkInterface.cs) own the hardware link to firmware. They exchange fixed-size transfer buffers, decode packet headers, and route requests such as object-model updates, file operations, code-buffer notifications, and firmware-update traffic.
2. **Code path**. [Codes/CodeProcessorService.cs](Codes/CodeProcessorService.cs) coordinates codes across the DSF channels. Codes move through start, pre, internal processing, post, firmware, and executed stages so plugins and DSF internals can intercept or resolve them at defined points.
3. **IPC path**. [IPC/Server.cs](IPC/Server.cs) accepts UNIX-socket clients, negotiates their connection mode, and dispatches them to processors for commands, subscriptions, interceptions, code streams, and plugin-service coordination.
4. **Model path**. [Model/UpdateService.cs](Model/UpdateService.cs), [Model/PeriodicUpdateService.cs](Model/PeriodicUpdateService.cs), and [Model/SbcTriggerService.cs](Model/SbcTriggerService.cs) merge data coming from firmware, track sequence numbers, and make filtered updates available to subscribers.
5. **Filesystem and job path**. [Files/FilePathResolver.cs](Files/FilePathResolver.cs) maps firmware-style paths like `0:/sys/config.g` to the Linux filesystem, while [Files/JobProcessor.cs](Files/JobProcessor.cs) tracks the active print and macro execution state.

## Interfaces With Other DSF Projects

| Peer | Interface |
|---|---|
| [../DuetWebServer/README.md](../DuetWebServer/README.md) | DWS connects over the IPC socket for commands and object-model subscriptions. |
| [../DuetPluginService/README.md](../DuetPluginService/README.md) | Plugin-service instances connect over the plugin-service IPC mode to install, start, stop, and supervise plugins. |
| [../DuetAPIClient/README.md](../DuetAPIClient/README.md) consumers | Tools and plugins connect over the command, intercept, subscribe, and code-stream IPC modes. |
| [../DuetAPI/README.md](../DuetAPI/README.md) | Supplies the object-model and command contracts that DCS owns at runtime. |
| [../DuetSharedLibrary/README.md](../DuetSharedLibrary/README.md) | Supplies shared log, version, path, and helper code. |

## Relationship To RepRapFirmware

This is the DSF project with the direct firmware connection. `DuetControlServer` is the SPI master in SBC mode and is responsible for:

- sending codes to RRF when they reach the firmware stage of the pipeline;
- receiving object-model deltas and merging them into the DSF object model;
- proxying file operations on behalf of firmware;
- keeping per-channel state aligned with RRF's code buffers and macro stack;
- initiating in-application firmware updates when run with `--update`.

No other DSF service talks directly to RepRapFirmware. They all go through DCS.

## Runtime Inputs And Outputs

- Configuration: `/opt/dsf/conf/config.json`
- IPC socket: `/var/run/dsf/dcs.sock`
- Virtual SD root: `/opt/dsf/sd`
- Plugin directory: `/opt/dsf/plugins`
- Logs: typically `/var/log/dsf/`

## Build And Verify

```sh
dotnet build DuetControlServer.csproj
dotnet run --project DuetControlServer.csproj -- --help
```

End-to-end verification requires a reachable Duet board or a development setup that can satisfy the link layer. For local service debugging, the top-level [../../README.md](../../README.md) and [../../docs/devel/BUILD_VARIANTS.md](../../docs/devel/BUILD_VARIANTS.md) describe the usual `systemctl stop` plus direct-run workflow.

## Related Docs

- [../../docs/devel/DCS_INTERNALS.md](../../docs/devel/DCS_INTERNALS.md)
- [../../docs/devel/CODE_PIPELINE.md](../../docs/devel/CODE_PIPELINE.md)
- [../../docs/devel/SPI_LINK.md](../../docs/devel/SPI_LINK.md)
- [../../docs/devel/IPC_PROTOCOL.md](../../docs/devel/IPC_PROTOCOL.md)
- [../../docs/devel/FILES.md](../../docs/devel/FILES.md)
- [../../docs/devel/OBJECT_MODEL.md](../../docs/devel/OBJECT_MODEL.md)
