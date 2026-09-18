#!/usr/bin/env bash
# Redirect Claude Code's per-project state out of the container home and into the
# workspace, which is a bind mount from the host and therefore survives a rebuild.
#
# Most of .claude/ is gitignored. Durable knowledge (memory, plans) sits at the
# top level where it is easy to find and back up; churning session state
# (transcripts, sessions, file history, backups) is kept apart under
# .claude/local/. Of these, only .claude/memory/ is committed, so memories are
# shared with everyone working on the repo.
#
# Memories that should not be shared go in .claude/memory/private/, which
# .gitignore excludes. It is a real directory in the workspace rather than a
# link into the container home, so it survives a rebuild like everything else
# here, and it sits inside the memory directory so Claude reads it as part of
# the same set. Its index is .claude/memory/private/MEMORY.md; the committed
# .claude/memory/MEMORY.md must not name private memories.
#
# Credentials are deliberately left in the container home: a rebuild loses the
# login, which is the price of keeping a secret out of the working tree.
#
# Idempotent: safe to run on every container start.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HOME_CLAUDE="${HOME}/.claude"

# Claude Code names each project directory after its path with the separators
# replaced by dashes, e.g. /workspaces/Foo -> -workspaces-Foo.
SLUG="${REPO//\//-}"
HOME_PROJECT="${HOME_CLAUDE}/projects/${SLUG}"

# Point a path in the container home at a directory in the workspace, migrating
# anything already there. Existing files in the workspace win, so a rebuild that
# starts with a fresh home never overwrites persisted state.
link() {
	local home_path="$1" repo_path="$2"

	mkdir -p "${repo_path}"

	if [ -L "${home_path}" ]; then
		# Already linked; re-point it in case the destination moved.
		ln -sfn "${repo_path}" "${home_path}"
		return
	fi

	if [ -e "${home_path}" ]; then
		cp -an "${home_path}/." "${repo_path}/" 2>/dev/null || true
		rm -rf "${home_path}"
	fi

	mkdir -p "$(dirname "${home_path}")"
	ln -sfn "${repo_path}" "${home_path}"
}

mkdir -p "${HOME_CLAUDE}/projects"

# Transcripts and the rest of the project directory. The memory subdirectory is
# re-pointed at .claude/memory below, so it does not stay buried under local/.
link "${HOME_PROJECT}" "${REPO}/.claude/local/projects/${SLUG}"
link "${HOME_PROJECT}/memory" "${REPO}/.claude/memory"

# The private half of the memory directory, and the index that lists it. Seeded
# only when absent, so an existing index is never overwritten.
PRIVATE_MEMORY="${REPO}/.claude/memory/private"
mkdir -p "${PRIVATE_MEMORY}"
if [ ! -e "${PRIVATE_MEMORY}/MEMORY.md" ]; then
	cat >"${PRIVATE_MEMORY}/MEMORY.md" <<-'EOF'
		<!-- Index of memories that are not committed. Same one-line-per-memory
		     format as ../MEMORY.md, and the memory files live beside this one. -->
	EOF
fi

# Sessions started inside .claude/worktrees/ get their own project directory,
# named after the worktree path. Catch any that already exist.
for dir in "${HOME_CLAUDE}/projects/${SLUG}-"*; do
	[ -e "${dir}" ] || continue
	name="$(basename "${dir}")"
	link "${dir}" "${REPO}/.claude/local/projects/${name}"
done

link "${HOME_CLAUDE}/plans" "${REPO}/.claude/plans"
link "${HOME_CLAUDE}/sessions" "${REPO}/.claude/local/sessions"
link "${HOME_CLAUDE}/file-history" "${REPO}/.claude/local/file-history"
link "${HOME_CLAUDE}/backups" "${REPO}/.claude/local/backups"

echo "Claude state persisted under ${REPO}/.claude"
