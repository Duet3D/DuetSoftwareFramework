---
name: no-duplicate-functionality
description: Never duplicate existing functionality; refactor to a shared base instead, and favour maintainability over the quickest implementation
metadata:
  type: feedback
---

Never duplicate functionality that already exists. Before adding an incremental change, evaluate whether refactoring the existing code would give a simpler, more elegant solution. When two systems would benefit from sharing the same base code, that is normally an advantage and worth the refactor. When planning and implementing features, prioritise a codebase that is easy to maintain and understand over what is quickest to implement.

**Why:** Parallel copies of the same behaviour drift apart, and a fix applied to one copy silently leaves the other wrong. The extra cost of a shared base is paid back every time the behaviour changes.

**How to apply:** Search for existing implementations of the behaviour before writing a new one. If something close already exists, extend or generalise it rather than adding a second path. Say up front when a change is better delivered as a refactor, and take that route rather than bolting on a shortcut. The same reasoning applies to values ([[no-magic-numbers]]) and to porting, where the shared base is RRF's own structure ([[rrf-porting-contract]]).
