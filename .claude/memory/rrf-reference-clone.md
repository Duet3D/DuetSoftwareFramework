---
name: rrf-reference-clone
description: lib/RepRapFirmware is a read-only reference checkout for the feature migration, not part of this project
metadata:
  type: project
---

RepRapFirmware is cloned into [lib/RepRapFirmware/](lib/RepRapFirmware/) purely as a reference while features are migrated out of it (see [[dsf-architecture-migration]]). It is not a submodule and not part of the project — .gitignore excludes it.

**Why:** It sits under `lib/` alongside genuinely editable subprojects ([[editable-vendored-projects]]), so it can be mistaken for code to build or change.

**How to apply:** Read it to see how RRF implements something being ported, but never edit it, and never treat it as a build dependency or as evidence of how this project currently works.
