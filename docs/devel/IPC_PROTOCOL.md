# IPC Protocol

DCS exposes a UNIX-domain socket for inter-process communication. **Every** non-DCS process that needs machine state or wants to influence behaviour talks to this socket — DWS, plugin services, plugins, command-line tools (`CodeConsole`, `CodeStream`, `ModelObserver`, `PluginManager`), third-party integrations.

The socket lives at `/var/run/dsf/dcs.sock` (override with `--socket-directory` / `--socket-file`).

## 1. Connection lifecycle

```mermaid
sequenceDiagram
    autonumber
    participant Client
    participant DCS
    Client->>DCS: connect(/var/run/dsf/dcs.sock)
    DCS-->>Client: ServerInitMessage(version, sessionId, machineMode)
    Client->>DCS: ClientInitMessage(mode, version, ...)
    alt mode supported & version compatible
      DCS-->>Client: BaseResponse(success)
      Note over Client,DCS: connection enters chosen processor
    else
      DCS-->>Client: BaseResponse(error)
      DCS->>DCS: close socket
    end
    loop until disconnect
      Client<<->>DCS: JSON commands / responses (per mode)
    end
```

Init messages are `\n`-terminated JSON objects ([`InitMessage` hierarchy](../../src/DuetAPI/Connection/InitMessages)). Negotiation is mandatory — the server has a `MinimumProtocolVersion` (currently 7) and refuses older clients. After negotiation, the per-mode protocol takes over.

## 2. Connection modes

The `ConnectionMode` enum ([`Modes/ConnectionMode.cs`](../../src/DuetAPI/Connection/Modes/ConnectionMode.cs)):

| Mode | Purpose | Processor |
|---|---|---|
| `Command` | Issue commands (codes, file ops, M409, plugin install, etc.). Each command is an atomic request → response. | [`IPC.Processors.Command`](../../src/DuetControlServer/IPC/Processors/Command.cs) |
| `Intercept` | Receive codes at one of `Pre` / `Post` / `Executed` and resolve / cancel / ignore them. | [`IPC.Processors.CodeInterception`](../../src/DuetControlServer/IPC/Processors/CodeInterception.cs) |
| `Subscribe` | Receive object-model deltas (or full patches) push-style. | [`IPC.Processors.ModelSubscription`](../../src/DuetControlServer/IPC/Processors/ModelSubscription.cs) |
| `CodeStream` | Stream codes asynchronously without waiting for replies before sending the next. | [`IPC.Processors.CodeStream`](../../src/DuetControlServer/IPC/Processors/CodeStream.cs) |
| `PluginService` | Internal mode used only by the two `DuetPluginService` instances. | [`IPC.Processors.PluginService`](../../src/DuetControlServer/IPC/Processors/PluginService.cs) |

```mermaid
flowchart LR
    Sock[(dcs.sock)] --> Server[IPC.Server]
    Server --> ProcF{ProcessorFactory}
    ProcF --> CMD[Command]
    ProcF --> INT[Intercept]
    ProcF --> SUB[Subscribe]
    ProcF --> CS[CodeStream]
    ProcF --> PS[PluginService]
```

## 3. Command mode

Every interaction is `Command` → `Response`. Commands are JSON objects, dispatched by `CommandFactory` to one of the typed `Command` classes under [`Commands/`](../../src/DuetControlServer/Commands).

```mermaid
sequenceDiagram
    participant Client
    participant DCS
    Client->>DCS: { "command": "Code", "code": "G1 X10" }
    DCS-->>Client: { "success": true, "result": { … CodeResult … } }
    Client->>DCS: { "command": "GetObjectModel", "key": "move.axes[0]" }
    DCS-->>Client: { "success": true, "result": { … } }
```

Important commands (full list in [`DuetAPI.Commands`](../../src/DuetAPI/Commands)):

| Command | Effect |
|---|---|
| `Code` | Run a single G/M/T-code through the pipeline. |
| `SimpleCode` | Run a code on a chosen channel (default `HTTP`). |
| `Flush` | Wait for all queued codes on a channel. |
| `GetObjectModel` | Retrieve a subtree of the object model. |
| `EvaluateExpression` | Evaluate an expression against the model. |
| `LockObjectModel` / `UnlockObjectModel` | Take the OM write lock from outside DCS. |
| `GetFileInfo` | Parse slicer metadata. |
| `Install`/`Start`/`Stop`/`Uninstall`/`SetPluginData` | Plugin life-cycle. |
| `WriteMessage` | Inject a message into the OM message log. |
| `AddHttpEndpoint` / `RemoveHttpEndpoint` | Custom HTTP endpoint registration (DWS picks them up). |
| `AddUserSession` / `RemoveUserSession` | Session management. |

## 4. Intercept mode

A plugin connects, declares which stage and which channels it cares about, and then receives codes one at a time. For each code the plugin must respond with one of `Resolve` / `Cancel` / `Ignore`.

```mermaid
sequenceDiagram
    participant Plug
    participant DCS
    Plug->>DCS: ClientInitMessage(mode=Intercept, type=Pre, channels=[HTTP,File])
    DCS-->>Plug: ack
    loop forever
      DCS->>Plug: { "command": "CodeInterception", code: {…}, … }
      alt resolve
        Plug-->>DCS: { "command":"Resolve", "type":"Success", "content":"OK" }
      else cancel
        Plug-->>DCS: { "command":"Cancel" }
      else ignore
        Plug-->>DCS: { "command":"Ignore" }
      end
    end
```

The plugin must answer **every** code within a small timeout — dragging the pipeline is a deployment-blocking bug.

## 5. Subscribe mode

Two flavours:

- **`Full`** — DCS sends the entire object model at connection time, then on every change pushes the entire (changed) subtree.
- **`Patch`** — DCS sends a JSON Merge Patch describing only what changed. Lower bandwidth, recommended for DWC-style consumers.

```mermaid
sequenceDiagram
    participant Sub
    participant DCS
    Sub->>DCS: ClientInitMessage(mode=Subscribe, mode=Patch, filter="move/**")
    DCS-->>Sub: full ObjectModel snapshot (filtered)
    loop on every model change
      DCS->>Sub: JSON Merge Patch
      Sub-->>DCS: ack (any non-empty JSON)
    end
```

A `filter` string narrows what the subscriber receives — a path expression with wildcards (`move/**`, `state/status`, `seqs`).

## 6. CodeStream mode

A throughput optimisation: instead of `Code` → `Response` ping-pong, the client opens a stream, sends codes as fast as it likes, and DSF replies whenever a code finishes. Used by [`CodeStream`](../../src/CodeStream) and by DWS for long lists of codes.

```mermaid
sequenceDiagram
    participant Client
    participant DCS
    Client->>DCS: ClientInitMessage(mode=CodeStream, bufferSize=8)
    Client->>DCS: G1 X10
    Client->>DCS: G1 X20
    Client->>DCS: G1 X30
    DCS-->>Client: result for G1 X10
    Client->>DCS: G1 X40
    DCS-->>Client: result for G1 X20
    DCS-->>Client: result for G1 X30
    DCS-->>Client: result for G1 X40
```

`bufferSize` caps how many codes can be in flight at once.

## 7. PluginService mode

Reserved for the two `DuetPluginService` daemons. They register themselves so that DCS knows where to route plugin commands (start/stop/install). The two services have different privilege levels (root and dsf user) — the root one only handles plugins that explicitly need root.

## 8. Authentication

The IPC socket is filesystem-protected — the directory `/var/run/dsf` is owned by `dsf:dsf` with mode 0770, so any client must be in the `dsf` group to connect. There is no password / token; identity is the OS user.

External authentication (sessions, passwords) is layered on top by DWS at the HTTP boundary, not at the IPC layer.

## 9. Library wrappers

Most clients use [`DuetAPIClient`](../../src/DuetAPIClient) instead of speaking the protocol directly:

```csharp
using var conn = new CommandConnection();
await conn.Connect();
var reply = await conn.PerformSimpleCode("M409 K\"move\"");
```

Specialised connection classes wrap each mode: `CommandConnection`, `InterceptConnection`, `SubscribeConnection`, `CodeStreamConnection`, etc.

For non-.NET languages, the protocol is plain JSON-over-Unix-socket — Python plugins just use `socket` + `json`.

## 10. Where this connects to the rest of the system

- Plugins live on top of `Intercept` and `Command` modes — see [PLUGINS.md](PLUGINS.md).
- DWS uses `Command` and `Subscribe` modes — see [HTTP_API.md](HTTP_API.md).
- Subscribe-mode delivery is built on the model differ — see [OBJECT_MODEL.md](OBJECT_MODEL.md).
- The full IPC schema (every command, every response, every init message) is generated as `OpenAPI.yaml` — see also `/api/` in the published DocFX site.
