# Duet3D System Architecture

This directory contains **cross-repository** architecture documentation that spans all three Duet3D firmware / software projects:

- **[RepRapFirmware](https://github.com/Duet3D/RepRapFirmware)** — printer-controller firmware on Duet 3 main boards. Per-repo docs: `RepRapFirmware/docs/devel/`.
- **[Duet3Expansion](https://github.com/Duet3D/Duet3Expansion)** — firmware on CAN-attached tool / expansion boards. Per-repo docs: `Duet3Expansion/docs/devel/`.
- **[DuetSoftwareFramework](https://github.com/Duet3D/DuetSoftwareFramework)** — .NET service stack on a Linux SBC. Per-repo docs: `DuetSoftwareFramework/docs/devel/`.

If you are working *inside* one repository, the per-repo `docs/devel/` is usually what you want. **This** directory is for understanding how the three pieces fit together.

## How to read these docs

| Document | What it covers |
|---|---|
| [SYSTEM_ARCHITECTURE.md](SYSTEM_ARCHITECTURE.md) | The whole-system diagram. Components, processes, protocols, deployment modes. The single document to read first if you are new to the ecosystem. |
| [GCODE_FLOW.md](GCODE_FLOW.md) | A worked end-to-end trace of a single G-code from a browser click to a stepper pulse on a tool board. Each hop annotated with the relevant per-repo source files. |
| [COMMUNICATION_PROTOCOLS.md](COMMUNICATION_PROTOCOLS.md) | Side-by-side reference for the four protocols that knit the system together: HTTP/WebSocket, IPC (Unix socket), SPI (DSF↔RRF), CAN-FD (RRF↔expansion). |
| [DEPLOYMENT_MODES.md](DEPLOYMENT_MODES.md) | What changes between **standalone**, **SBC**, and **CAN-expansion** deployments — and which combinations are supported. |
| [EXECUTION_CALL_DIAGRAMS.md](EXECUTION_CALL_DIAGRAMS.md) | Class/module maps and function-level call diagrams for the major standalone and SBC execution paths, including printing, macros, OM requests, meta G-code, and expansion-board flows. |
| [OBJECT_MODEL_END_TO_END.md](OBJECT_MODEL_END_TO_END.md) | The flow of state from a device on a tool board, up the bus, through RRF, across the SPI link, into DSF, and out to a browser. The single contract that ties the whole system together. |
| [COMPONENT_INTERACTION_MATRIX.md](COMPONENT_INTERACTION_MATRIX.md) | DSF-project keyed map of which RepRapFirmware and Duet3Expansion components each DSF project actually interacts with. |
| [COMPATIBILITY.md](COMPATIBILITY.md) | Cross-repo version contracts: which versions of each repo work together, where the contracts live in source, and what breaks first when they go out of sync. |

## Conventions used

- **Mermaid** is used for diagrams; GitHub renders them inline.
- Cross-repository links use relative paths from this file's location, e.g. `../../../RepRapFirmware/docs/devel/`. They assume the three repos are checked out as siblings under a common parent, which is the layout the developer documentation in each repo expects.
- "RRF" = RepRapFirmware. "DSF" = DuetSoftwareFramework. "DCS" = DuetControlServer (the heart of DSF). "DWS" = DuetWebServer. "DWC" = Duet Web Control (the browser SPA, separate repo).
