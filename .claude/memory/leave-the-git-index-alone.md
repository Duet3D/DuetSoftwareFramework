---
name: leave-the-git-index-alone
description: Never unstage or restage what the user staged; when told to commit only some files, commit those paths explicitly instead of adjusting the index
metadata:
  type: feedback
---

Do not run `git restore --staged`, `git reset` or `git add -A` over work the user staged themselves. If
a commit is meant to contain only some files, name those paths in `git commit -- <paths>` or stage only
them, and leave everything else in the index exactly as it was found.

**Why:** staging is how the user marks what they consider ready. Unstaging it destroys that decision
silently, and they have to notice and redo it. It also reads as tidying up after them.

**How to apply:** before committing, check `git status --short` to see what is already staged, then
commit the intended paths explicitly rather than reshaping the index around them. Verify afterwards
with `git show --stat` that the commit holds what was intended - that is the check that matters, not
what the index looked like.
