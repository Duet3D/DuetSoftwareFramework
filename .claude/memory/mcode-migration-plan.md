---
name: mcode-migration-plan
description: Where the RepRapFirmware migration plans live in docs/devel and what each one covers
metadata:
  type: project
---

The migration of RepRapFirmware's `GCodes::HandleMcode` into DuetControlServer is tracked in `docs/devel/MCODE_MIGRATION.md` on the `experimental/feat/motion` line of work. It holds the full inventory of ~190 M-codes with their object model homes and status, the porting contract ([[rrf-porting-contract]]), the macro inventory, and a decisions section recording why things were done a particular way.

Sibling docs, same conventions:

- `docs/devel/EVENTS_MIGRATION.md`: RRF's event system (the `Event` queue, event macros, `M957`) and the two DSF-only link events `controller_disconnect` / `controller_reconnect`.
- `docs/devel/STALL_DETECTION.md`: the plan for making `M574 S3`/`S4` stall homing work, with a phase status table.
- `docs/devel/INPUT_MONITORS.md`: the plan for the unsent `CanMessageChangeInputMonitorV1`, covering probe threshold and report interval, deleting abandoned monitors, and the deliberate divergences.
- `docs/devel/PROJECT_OVERVIEW.md`: the rollup whose counts and dependency graph derive from the plans above.

**Why:** These are the record of decisions already taken and gaps already recorded, so work started without reading the covering plan risks re-deriving or contradicting a settled choice.

**How to apply:** Read the covering plan before starting, and update it as part of the work per [[keep-plans-and-overview-current]] and [[plans-describe-current-state]]. The split against the published articles is in [[docs-plans-vs-articles]]; the object model rule every configuration code must satisfy is [[machine-state-in-object-model]].
