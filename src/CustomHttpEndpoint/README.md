# CustomHttpEndpoint

`CustomHttpEndpoint` is a helper tool for registering custom HTTP or WebSocket endpoints with `DuetWebServer`. It exists mainly as a reference implementation for third-party endpoint registration and as a quick way to bridge simple local executables into the DSF HTTP surface.

## How It Works

[Program.cs](Program.cs) accepts the HTTP method, namespace, path, and optional executable information for an endpoint under `/machine/{namespace}/{path}`. [Commands.cs](Commands.cs) then uses [../DuetAPIClient/README.md](../DuetAPIClient/README.md) to register that endpoint with DSF.

At runtime the flow is:

1. the tool registers endpoint metadata with DCS;
2. `DuetWebServer` exposes the route and forwards matching requests;
3. this tool either executes a local process and returns its output, or handles a WebSocket session interactively via stdin/stdout.

## Interfaces

| Interface | Details |
|---|---|
| DCS IPC | Command-mode registration of the custom endpoint. |
| DWS HTTP pipeline | Receives forwarded HTTP or WebSocket traffic once the route is registered. |
| Local processes | Optional executable launched to generate the HTTP response body. |
| RepRapFirmware | No direct interface. Any firmware interaction would happen only through additional DSF calls made by the executed program. |

## Typical Uses

- prototype a new `/machine/...` endpoint before turning it into a full plugin;
- expose shell-script or helper-program output through DWS;
- learn the endpoint-registration part of the DSF plugin API.

## Build And Verify

```sh
dotnet build CustomHttpEndpoint.csproj
dotnet run --project CustomHttpEndpoint.csproj -- --help
```

## Related Docs

- [../DuetWebServer/README.md](../DuetWebServer/README.md)
- [../../docs/devel/HTTP_API.md](../../docs/devel/HTTP_API.md)
- [../../docs/devel/IPC_PROTOCOL.md](../../docs/devel/IPC_PROTOCOL.md)
