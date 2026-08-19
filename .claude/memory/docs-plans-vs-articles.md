---
name: docs-plans-vs-articles
description: Plans and implementation reasoning go in docs/devel; user-facing current-state docs go in src/Documentation/articles and must contain no history or plans
metadata:
  type: project
---

Plans, design reasoning, and migration tracking belong in [docs/devel/](docs/devel/). User-facing explanations of what the code currently does belong in [src/Documentation/articles/](src/Documentation/articles/).

Articles describe **only the current state of the code** — no historical narrative, no "used to", no planned or future work. Anything not yet implemented stays in `docs/devel`.

**Why:** The two audiences are different: `docs/devel` is for whoever is doing the work, articles are for whoever is using the result. Mixing plans into articles makes them read as promises of behaviour that does not exist.

**How to apply:** When writing up work, split it — reasoning and remaining steps into a `docs/devel` file, and only the shipped behaviour into the relevant article. See [[rrf-differences-article]] for the strictest case.
