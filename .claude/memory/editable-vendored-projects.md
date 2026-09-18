---
name: editable-vendored-projects
description: The lib/ subprojects and src/Duet3Expansion, src/DuetCANMaster, src/DuetSbcInterface are in scope for edits, not read-only vendored code
metadata:
  type: project
---

Everything under [lib/](lib/) (CANlib, CoreN2G, DuetSpiInterface, FreeRTOS, LibMbedTls, LibTinyusb, Qfplib-M0-full, RRFLibraries, plus the .cmake files) may be modified, as may [src/Duet3Expansion/](src/Duet3Expansion/), [src/DuetCANMaster/](src/DuetCANMaster/), and [src/DuetSbcInterface/](src/DuetSbcInterface/).

Exception: [lib/RepRapFirmware/](lib/RepRapFirmware/) is a read-only reference checkout, not part of the project — see [[rrf-reference-clone]].

**Why:** They look like vendored/third-party dependencies, so the default instinct is to work around them instead of fixing them. The user owns these and wants fixes made at the source.

**How to apply:** When a change is cleanest inside one of these projects, make it there rather than adding a workaround in the calling code. No need to ask whether they are off-limits.
