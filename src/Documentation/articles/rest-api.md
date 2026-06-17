# REST API

[DuetWebServer](components.md#duetwebserver) exposes an HTTP REST API under `/machine/*`, used by
DuetWebControl and by any HTTP client (including [DuetHttpClient](components.md#duethttpclient) in SBC
mode). It also serves the legacy RepRapFirmware-compatible `rr_*` endpoints for backward
compatibility. Every request is proxied to DCS over the [IPC socket](ipc.md).

All endpoints except `connect` require an `X-Session-Key` header (or, for the WebSocket, a
`sessionKey` query parameter); a missing or invalid key returns `403`.

## Authentication and sessions

| Method & path | Purpose |
| --- | --- |
| `GET /machine/connect?password={password}` | Authenticate (default password `reprap`) and obtain a session key. Returns `{"sessionKey": <key>}` |
| `GET /machine/noop` | Keep-alive / ping (`204`) |
| `GET /machine/disconnect` | Close the session (`204`) |

`GET /machine/connect` status codes: `200` OK, `403` forbidden (wrong password), `500` internal error,
`502` incompatible DCS version, `503` DCS unavailable.

## Object model

| Method & path | Purpose |
| --- | --- |
| `GET /machine/model` | Return the full [object model](object-model.md) as JSON |
| `WS /machine?sessionKey={key}` | Subscribe to model updates over a WebSocket |

The WebSocket sends the complete model first, then JSON [patches](object-model.md#observing-changes-and-patches)
for each change. The client replies `OK\n` to acknowledge each message and may send `PING\n` (the
server answers `PONG\n`) as a keep-alive. This path is backed by a [`Subscribe` connection](ipc.md#subscribe)
to DCS.

## Code execution

| Method & path | Purpose |
| --- | --- |
| `POST /machine/code?async={true\|false}` | Run the G/M/T-code in the request body |

The reply is returned as `text/plain`. With `async=true` the request returns once the code is
scheduled rather than once it completes. The code enters the [pipeline](gcode-flow.md) on the `HTTP`
channel.

## Files and directories

| Method & path | Purpose |
| --- | --- |
| `GET /machine/file/{filename}` | Download a file |
| `PUT /machine/file/{filename}?timeModified={iso8601}` | Upload a file (optional modified time) |
| `DELETE /machine/file/{filename}?recursive={true\|false}` | Delete a file or directory |
| `POST /machine/file/move` | Move/rename (form fields `from`, `to`, optional `force`) |
| `GET /machine/fileinfo/{filename}?readThumbnailContent={true\|false}` | Parse [G-code metadata](file-management.md#file-info-parsing) |
| `GET /machine/directory/{directory}` | List a directory (JSON array of `{type, name, date, size}`) |
| `PUT /machine/directory/{directory}` | Create a directory |

Filenames use [virtual paths](file-management.md#path-mapping) (for example `0:/gcodes/test.gcode`).

## Plugins

| Method & path | Purpose |
| --- | --- |
| `PUT /machine/plugin` | Install or upgrade a [plugin](plugins.md) (ZIP body) |
| `DELETE /machine/plugin` | Uninstall a plugin |
| `PATCH /machine/plugin` | Set plugin data (`{plugin, key, value}`) |
| `POST /machine/startPlugin` | Start an SBC plugin |
| `POST /machine/stopPlugin` | Stop an SBC plugin |

## Legacy `rr_*` endpoints

DuetWebServer also implements the RepRapFirmware HTTP endpoints (`rr_connect`, `rr_disconnect`, ...)
so that tools written for standalone firmware keep working. These responses report `isEmulated=true`
so a client such as [DuetHttpClient](components.md#duethttpclient) can detect that it is talking to an
SBC and switch to the richer `/machine/*` API.

## See also

- [Components](components.md#duetwebserver) - how DuetWebServer is built
- [IPC](ipc.md) - the DCS connections these endpoints proxy to
- [Object model](object-model.md) - what `GET /machine/model` and the WebSocket return
