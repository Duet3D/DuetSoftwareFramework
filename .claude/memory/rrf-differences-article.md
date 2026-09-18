---
name: rrf-differences-article
description: src/Documentation/articles/rrf-differences.md lists only deliberate, implemented deviations from RRF — never unported gaps or planned work
metadata:
  type: project
---

[src/Documentation/articles/rrf-differences.md](src/Documentation/articles/rrf-differences.md) is the record of deliberate functional deviations from RepRapFirmware made during the feature migration ([[dsf-architecture-migration]]).

Every entry must be present, working, and intentionally different. Not-yet-ported features are **gaps, not differences** — they belong in the `docs/devel` migration files ([[docs-plans-vs-articles]]), never here.

**Why:** A gap listed as a difference reads as a settled decision, so nobody goes back and finishes it — and the divergence only surfaces much later, when the missing piece lands.

**How to apply:** Before adding an entry, check it is shipped behaviour someone deliberately chose, and record why it was chosen. If it is still planned or blocked, put it in `docs/devel` instead.
