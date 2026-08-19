---
name: rrf-porting-contract
description: When porting features from RRF, replicate behaviour exactly; ask permission before any deviation, and shim unported dependencies with a TODO rather than changing behaviour
metadata:
  type: feedback
---

When migrating a feature from RepRapFirmware ([[rrf-reference-clone]]), replicate its functionality **exactly**. If exact replication is not possible, stop and ask for explicit permission, giving the reasoning for why the deviation is needed — do not decide it unilaterally.

If the feature being ported depends on another feature that has not been ported yet, **do not change the functionality to make it compile**. Write the minimal shim for the missing piece with a `TODO` comment describing what still needs to be added.

**Why:** A deviation that produces the right answer today is the hardest kind of bug to find later — the code looks considered, and the difference only surfaces when the missing piece lands and nobody remembers it was traded away. Approved deviations get recorded in [[rrf-differences-article]]; a shim with a TODO stays visible as a gap instead.

**How to apply:** Port line-for-line behaviour, including edge cases and error handling. Reach for a `// TODO:` shim naming the unported dependency, never an invented substitute behaviour. Raise any structural departure as a question before writing it.
