# DuetWebServer

`DuetWebServer` (DWS) is the HTTP and WebSocket front end of DSF. It serves browser clients, exposes the machine API, hosts the RRF-compatible `rr_*` endpoints, and relays custom third-party HTTP endpoints, while using `DuetControlServer` as its backend.

## At A Glance

| Aspect | Details |
|---|---|
| Entry point | [Program.cs](Program.cs) and [Startup.cs](Startup.cs) |
| Runtime type | ASP.NET Core service hosted on Kestrel |
| Main config | `/opt/dsf/conf/http.json` via [Settings.cs](Settings.cs) |
| Key areas | [Controllers/](Controllers), [Middleware/](Middleware), [Services/](Services), [Singletons/](Singletons), [Authorization/](Authorization), [FileProviders/](FileProviders) |

## What This Project Owns

- The HTTP API surfaced to browsers, scripts, and third-party integrations.
- WebSocket endpoints used for live object-model updates and command streams.
- Session tracking and authentication glue for HTTP clients.
- The RRF-compatible HTTP layer implemented in [Controllers/RepRapFirmwareController.cs](Controllers/RepRapFirmwareController.cs).
- Static-file serving for DWC and other web assets, including virtual-SD-backed content.
- Runtime dispatch for custom HTTP endpoints registered by plugins and tools.

## How It Works

[Program.cs](Program.cs) creates the ASP.NET host and [Startup.cs](Startup.cs) wires together middleware, authentication, controllers, and hosted services. The core execution flow looks like this:

1. `DuetWebServer` opens a command connection and a subscription connection to DCS using [../DuetAPIClient/README.md](../DuetAPIClient/README.md).
2. [Services/ModelObserver.cs](Services/ModelObserver.cs) keeps [Singletons/ModelProvider.cs](Singletons/ModelProvider.cs) synchronized with the live object model.
3. Controllers and middleware use that data plus command calls back into DCS to satisfy browser and API requests.
4. [Singletons/SessionStorage.cs](Singletons/SessionStorage.cs) and [Authorization/SessionKeyAuthenticationHandler.cs](Authorization/SessionKeyAuthenticationHandler.cs) manage session-based access.
5. [Middleware/CustomEndpointMiddleware.cs](Middleware/CustomEndpointMiddleware.cs) routes requests for dynamically registered endpoints to the owning plugin/tool implementation.

## Interfaces With Other DSF Projects

| Peer | Interface |
|---|---|
| [../DuetControlServer/README.md](../DuetControlServer/README.md) | Backend over IPC for commands, object-model subscriptions, file operations, and endpoint registration. |
| [../DuetAPI/README.md](../DuetAPI/README.md) | Shared object-model and command contracts used in controllers and authentication decisions. |
| [../DuetAPIClient/README.md](../DuetAPIClient/README.md) | Connection layer used to talk to DCS. |
| Plugins and tools | Can register custom endpoints that DWS exposes under `/machine/{namespace}/{path}`. |
| Browser clients and DWC | Talk to DWS over HTTP and WebSocket; DWS shields them from the IPC details behind the scenes. |

## Relationship To RepRapFirmware

`DuetWebServer` has no direct hardware link to RepRapFirmware. Its firmware-facing behavior is mediated by DCS:

- browser commands go to DWS, then over IPC to DCS, then to RRF if needed;
- object-model updates originate in RRF, are merged by DCS, and then delivered to DWS subscribers;
- `rr_*` compatibility endpoints emulate the firmware HTTP surface on top of the DCS backend.

## Runtime Inputs And Outputs

- Configuration: `/opt/dsf/conf/http.json`
- Default static web directory: `/opt/dsf/sd/www`
- Optional HTTPS certificate path used by DSF tooling: `/opt/dsf/conf/https.pfx`
- Primary backend socket: `/var/run/dsf/dcs.sock`

## Build And Verify

```sh
dotnet build DuetWebServer.csproj
dotnet run --project DuetWebServer.csproj -- --help
```

Functional testing usually means running DCS, starting DWS, then checking a browser or `curl` against the configured HTTP port.

## Related Docs

- [../../docs/devel/HTTP_API.md](../../docs/devel/HTTP_API.md)
- [../../docs/devel/IPC_PROTOCOL.md](../../docs/devel/IPC_PROTOCOL.md)
- [../../docs/devel/OBJECT_MODEL.md](../../docs/devel/OBJECT_MODEL.md)
- [../../docs/devel/ARCHITECTURE.md](../../docs/devel/ARCHITECTURE.md)
