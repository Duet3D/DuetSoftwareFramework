# Cross-Repo Component Interaction Matrix

This document maps the **DuetSoftwareFramework project structure** onto the specific RepRapFirmware and Duet3Expansion components it interacts with.

The matrix is keyed by DSF project because DSF is the only repository in this workspace that is structured as a multi-project solution. RepRapFirmware and Duet3Expansion are both monolithic firmware trees, so their side of the matrix is expressed in terms of source modules and representative files.

## Reading the matrix

- **Direct** means the DSF project is the code that actually speaks to the peer component.
- **Indirect** means the DSF project only reaches the peer through another DSF project, usually `DuetControlServer`.
- **No direct interface** is explicit when a component only participates through higher-level state or documentation.

| DSF project | RepRapFirmware components | Duet3Expansion components | Interaction |
|---|---|---|---|
| [DuetAPI](../../src/DuetAPI/README.md) | [ObjectModel](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/ObjectModel/README.md), [GCodes](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/GCodes/README.md) channel enum contract | No direct interface; D3E state only appears once RRF folds it into the object model | Defines the DSF-side schema that mirrors RRF machine state and channel concepts. |
| [DuetAPI.SourceGenerators](../../src/DuetAPI.SourceGenerators/README.md) | [ObjectModel](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/ObjectModel/README.md) descriptor-driven schema, indirectly | No direct interface | Generates DSF boilerplate around a model that is kept compatible with RRF's reflected state. |
| [DuetAPIClient](../../src/DuetAPIClient/README.md) | Indirect via [SBC](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/SBC/README.md) only when used by DCS-backed clients | No direct interface | Client-side IPC wrapper for DCS; it does not talk to firmware directly. |
| [DuetSharedLibrary](../../src/DuetSharedLibrary/README.md) | No direct interface | No direct interface | Internal DSF helper library only. |
| [DuetHttpClient](../../src/DuetHttpClient/README.md) | [Networking](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/Networking/README.md), [ObjectModel](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/ObjectModel/README.md), [Storage](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/Storage/README.md) | No direct interface | Talks to standalone RRF HTTP endpoints or the DWS compatibility surface that mirrors them. |
| [DuetControlServer](../../src/DuetControlServer/README.md) | [SBC](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/SBC/README.md), [GCodes](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/GCodes/README.md), [ObjectModel](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/ObjectModel/README.md), [Storage](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/Storage/README.md) | No direct transport; D3E state arrives through [CAN](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/CAN/README.md) and the RRF object model | The direct DSF peer of RRF. Owns SPI/USB link, code handoff, object-model sync, and virtual-SD proxying. |
| [DuetWebServer](../../src/DuetWebServer/README.md) | Indirect RRF interaction via DCS, plus semantic compatibility with [Networking](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/Networking/README.md) `rr_*` behavior | No direct interface | Browser/API front end that mirrors standalone RRF HTTP semantics on top of DCS. |
| [DuetPluginService](../../src/DuetPluginService/README.md) | Indirect via DCS and the RRF [GCodes](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/GCodes/README.md) path that plugins may affect | No direct interface | Manages DSF plugins but does not speak to RRF or D3E directly. |
| [DuetPiManagementPlugin](../../src/DuetPiManagementPlugin/README.md) | Semantic overlap with [Networking](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/Networking/README.md) and [Storage](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/Storage/README.md) because it intercepts standalone-style management codes before they reach RRF | No direct interface | Implements SBC-host management behaviors that mimic some standalone-era control flows. |
| [CodeConsole](../../src/CodeConsole/README.md) | Indirect via DCS into [GCodes](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/GCodes/README.md) | Indirect only through whatever RRF later sends over [CAN](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/CAN/README.md) | Sends codes to DCS, which may forward them to RRF. |
| [CodeLogger](../../src/CodeLogger/README.md) | Indirect observation of the DCS-to-[GCodes](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/GCodes/README.md) handoff | No direct interface | Observes DSF pipeline stages, including the point before firmware handoff. |
| [CodeStream](../../src/CodeStream/README.md) | Indirect via DCS into [GCodes](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/GCodes/README.md) | Indirect only through RRF's downstream CAN behavior | Buffered code sender aimed at DCS rather than firmware directly. |
| [CustomHttpEndpoint](../../src/CustomHttpEndpoint/README.md) | No direct interface | No direct interface | Registers DWS endpoints only. Any firmware interaction would be via extra DSF calls. |
| [ModelObserver](../../src/ModelObserver/README.md) | [ObjectModel](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/ObjectModel/README.md) via DCS replication | No direct interface; D3E-backed board state may appear inside the observed model | Observes the DSF-side mirror of RRF state. |
| [PluginManager](../../src/PluginManager/README.md) | No direct interface | No direct interface | Plugin lifecycle tool operating entirely on the DSF side. |
| [DocGen](../../src/DocGen/README.md) | Mirrors the shape of [ObjectModel](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/ObjectModel/README.md) indirectly through `DuetAPI` | No direct interface | Generates DSF-side object-model docs from the DSF mirror of the firmware schema. |
| [Documentation](../../src/Documentation/README.md) | [docs/devel/](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/docs/devel/README.md) and related RRF docs are referenced as companion architecture material | [docs/devel/ARCHITECTURE.md](https://github.com/Duet3D/Duet3Expansion/blob/3.7-docker/docs/devel/ARCHITECTURE.md) and related D3E docs are referenced as companion material | Documentation-only integration. |
| [UnitTests](../../src/UnitTests/README.md) | Validates behavior that must remain compatible with [SBC](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/SBC/README.md), [ObjectModel](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/ObjectModel/README.md), [Networking](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/Networking/README.md), and [GCodes](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/GCodes/README.md) semantics | No direct interface | Test coverage is DSF-side but many tests encode RRF compatibility expectations. |

## What never talks to Duet3Expansion directly from DSF

DSF has **no direct transport path** to Duet3Expansion firmware. All expansion-board interaction is mediated by RepRapFirmware:

1. DSF talks to RRF through DCS.
2. RRF talks to Duet3Expansion over CAN.
3. D3E state returns to DSF only after RRF folds it into diagnostics, replies, or the object model.

That architecture is why the most important D3E-related DSF project is still [DuetControlServer](../../src/DuetControlServer/README.md): it is the only DSF project that directly reaches the layer above CAN.

## High-value paths to understand first

- Browser/API control path: [DuetWebServer](../../src/DuetWebServer/README.md) → [DuetControlServer](../../src/DuetControlServer/README.md) → [RRF GCodes](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/GCodes/README.md)
- State path: [RRF ObjectModel](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/ObjectModel/README.md) → [DuetControlServer](../../src/DuetControlServer/README.md) → [DuetAPI](../../src/DuetAPI/README.md) → [DuetWebServer](../../src/DuetWebServer/README.md)
- Expansion path: [RRF CAN](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/src/CAN/README.md) → [Duet3Expansion CAN protocol](https://github.com/Duet3D/Duet3Expansion/blob/3.7-docker/docs/devel/CAN_PROTOCOL.md)

## Related docs

- [README.md](README.md)
- [DEPLOYMENT_MODES.md](DEPLOYMENT_MODES.md)
- [GCODE_FLOW.md](GCODE_FLOW.md)
- [RRF STANDALONE_VS_SBC.md](https://github.com/Duet3D/RepRapFirmware/blob/3.7-docker/docs/devel/STANDALONE_VS_SBC.md)
