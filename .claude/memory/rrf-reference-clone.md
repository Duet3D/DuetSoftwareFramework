---
name: rrf-reference-clone
description: lib/RepRapFirmware is a read-only reference checkout for the feature migration, not part of this project, and is where porting claims get checked
metadata:
  type: project
---

RepRapFirmware is cloned into [lib/RepRapFirmware/](lib/RepRapFirmware/), source under `lib/RepRapFirmware/src`, purely as a reference while features are migrated out of it (see [[dsf-architecture-migration]]). It is not a submodule and not part of the project; .gitignore excludes it. It is not under `src/`, where the four buildable programs live, so a search scoped to `src/` finds nothing and looks like the reference is absent.

Neither [src/Duet3Expansion/](src/Duet3Expansion/) nor [src/DuetCANMaster/](src/DuetCANMaster/) is upstream RRF. DuetCANMaster is RRF-derived but has had the code paths DSF does not use stripped out, so its silence about a feature is not evidence that RRF lacks it.

**Why:** It sits under `lib/` alongside genuinely editable subprojects ([[editable-vendored-projects]]), so it can be mistaken for code to build or change. It also makes a claim about "what RRF does" checkable rather than inferable: reasoning from the board side alone gives the wrong answer, for example `CanMessageChangeInputMonitorV1`'s `actionReturnPinName` is mostly used by RRF for the current-level value it returns rather than the pin name, and `actionDontMonitor` is never sent at all, neither of which is visible from the message definition.

**How to apply:** Read it to see how RRF implements something being ported, but never edit it, and never treat it as a build dependency or as evidence of how this project currently works. Before writing that DSF diverges from or is missing something RRF does, grep `lib/RepRapFirmware/src` for the call sites and read them. See [[rrf-porting-contract]] for what to do when the port does not fit.
