---
name: rrf-porting-contract
description: When porting features from RRF, replicate behaviour exactly; ask permission before any deviation, and shim unported dependencies with a TODO rather than changing behaviour
metadata:
  type: feedback
---

When migrating a feature from RepRapFirmware ([[rrf-reference-clone]]), replicate its functionality **exactly**, and do not depart from RRF's structure on your own judgement. If exact replication is not possible, stop and ask for explicit permission: say what RRF does, what does not fit here, and what the options are. Do not decide it unilaterally. This applies to subagents too; the rule is §1 rule 8 of `docs/devel/MCODE_MIGRATION.md` so it is inherited rather than remembered.

If the feature being ported depends on another feature that has not been ported yet, **do not change the functionality to make it compile**. Write the minimal shim for the missing piece with a `TODO` comment describing what still needs to be added.

**Why:** A deviation that produces the right answer today is the hardest kind of bug to find later. The code looks considered, so review passes it, and the difference only surfaces when the missing piece lands and nobody remembers what was traded away. The user caught one of these: a move carrying coordinates for only some of its axes was fixed by relocating `ToolOffsetTransform` instead of supplying the missing seed, which silently dropped the `explicitAxes` parameter tool axis mapping needs and the deferral that stops a tool offset change moving the axes during a pure extrusion. Both were unreachable until tools are ported, so nothing would have failed. Approved deviations get recorded in [[rrf-differences-article]]; a shim with a TODO stays visible as a gap instead.

**How to apply:** Port line-for-line behaviour, including edge cases and error handling. Reach for a `// TODO:` shim naming the unported dependency, never an invented substitute behaviour. Raise any structural departure as a question before writing it. When a symptom goes away by *moving* code rather than by supplying what was absent, treat that as the tell that the absent thing is still absent, and ask before committing to the shape.
