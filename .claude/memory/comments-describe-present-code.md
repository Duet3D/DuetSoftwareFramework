---
name: comments-describe-present-code
description: "Code comments must describe the code that is there, never what was removed or moved"
metadata:
  type: feedback
---

Do not write comments narrating what changed: "this used to do X", "X was moved here", "the two
that existed before disagreed", "no longer applied via Y". Applies to subagents too; the rule is
§1 rule 10 of `docs/devel/MCODE_MIGRATION.md` so it is inherited rather than remembered.

**Why:** The user's words: it is confusing. A reader of the current file has never seen X, cannot tell
whether X still exists somewhere else, and has to go and find out. A comment meant to save time
costs it. Commit messages and the migration doc are where history belongs, because both are read by
someone who is already looking for history.

**How to apply:** the useful half of such a comment is nearly always a statement about the present.
"One place knows this grammar, because two readers of one grammar diverge silently" carries
everything "there used to be two" carries, and stays true after the next change. Where a rule exists
because something went wrong, state the rule and the failure mode, not the incident. See
[[rrf-porting-contract]] and [[mcode-migration-plan]]. The same discipline applied to plan documents
is [[plans-describe-current-state]], and to the published articles [[docs-plans-vs-articles]].
