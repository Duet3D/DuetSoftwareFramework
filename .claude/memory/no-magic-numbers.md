---
name: no-magic-numbers
description: Never use magic numbers if avoidable; search for an existing setting, config value, or parameter before introducing a literal
metadata:
  type: feedback
---

Never use magic numbers where it can be avoided. Before writing a numeric literal, check whether an existing setting, config value, constant, or parameter already carries that value and use it instead. Only introduce a new named constant when nothing existing fits.

**Why:** A duplicated literal silently decouples from the setting it was copied from, so changing the setting no longer changes the behaviour — and the mismatch is invisible at the call site.

**How to apply:** Grep for the value and for the concept it represents (settings classes, existing constants, the RRF source when porting) before hardcoding. If the value is genuinely new, give it a named constant, or a field in the Object Model when it is machine configuration ([[machine-state-in-object-model]]). When porting, prefer the constant RRF itself uses ([[rrf-porting-contract]]).
