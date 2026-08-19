---
name: docs-plans-vs-articles
description: Plans and implementation reasoning go in docs/devel; user-facing current-state docs go in src/Documentation/articles and must contain no history or plans
metadata:
  type: project
---

Plans, design reasoning, and migration tracking belong in [docs/devel/](docs/devel/) (see [[mcode-migration-plan]] for what lives there). User-facing explanations of what the code currently does belong in [src/Documentation/articles/](src/Documentation/articles/).

Articles describe **only the current state of the code**: no historical narrative, no "used to", no planned or future work, no migration status. A document stays in `docs/devel` even once it is half implemented; nothing about unfinished work goes into an article.

When work changes how something important in DSF actually works, update the matching article in the same change.

**Why:** The two audiences are different: `docs/devel` is for whoever is doing the work, articles are published to the wiki and read by users of DSF. Anything speculative or historical in an article is actively misleading, and behaviour documented once and then left behind decays into wrong documentation, which is worse than none.

**How to apply:** When writing up work, split it: reasoning and remaining steps into a `docs/devel` file, and only shipped behaviour into the relevant article. After landing a change to behaviour a user can observe (a G/M-code's semantics, the object model, IPC, the firmware link, file handling, the REST API), find the article that covers it (`gcode-flow.md`, `object-model.md`, `ipc.md`, `firmware-link.md`, `spi-state-machine.md`, `rrf-differences.md`, `file-management.md`, `rest-api.md`, ...) and edit it to describe the new behaviour as if it had always been that way, per [[comments-describe-present-code]]. A genuinely new topic gets a new article plus an entry in `src/Documentation/articles/toc.yml`. See [[rrf-differences-article]] for the strictest case.
