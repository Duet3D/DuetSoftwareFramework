---
name: keep-plans-and-overview-current
description: When changing code, review and update the plans in docs/devel that cover it, and the rollup in docs/devel/PROJECT_OVERVIEW.md, in the same commit
metadata:
  type: feedback
---

Work in this tree is tracked by the plans in [docs/devel/](docs/devel/). Any change that lands, closes, or reopens something a plan describes must update that plan in the same commit: tick the phase, move the ✅/🟡/⬜ status, and write in anything the work turned out to need that the plan did not predict. Then update [docs/devel/PROJECT_OVERVIEW.md](docs/devel/PROJECT_OVERVIEW.md), the rollup whose task counts and dependency graph are derived from those plans and hold no facts of their own.

Before starting a change, read the plans covering the area. They carry decisions already taken and gaps already recorded, so acting without reading them risks re-deriving or contradicting a settled choice.

**Why:** Statuses drift in both directions, and the direction that looks harmless is worse. A row saying ⬜ for something that landed costs a duplicate implementation attempt, found within minutes. A row saying ✅ for something that does not work costs trust in every other ✅, and is found at the machine. MCODE_MIGRATION §17 records an audit that had to be run because the counts had gone stale; INPUT_MONITORS' summary table still contradicts its own phase sections.

**How to apply:** Treat the plan update as part of the change, not as follow-up work; a phase moves to ✅ in the commit that does it. Recount summary tables from the rows beneath them rather than adjusting them from memory of what changed. The arithmetic that catches a miscount is the status columns summing to the row count. When a plan and the overview disagree, the plan is correct and the overview is the one to fix. Write the update per [[plans-describe-current-state]], with no revision notes. Shipped behaviour also goes to the articles per [[docs-plans-vs-articles]] and [[rrf-differences-article]].
