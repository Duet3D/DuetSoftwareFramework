# pyright: strict
"""The submodules and the branches .gitmodules has them track: checking that each commit a revision
records is on its branch (scripts/submodule.py check, and the submodules check of scripts/doctor.py),
and updating, checking out and attaching them (scripts/submodule.py)."""

from __future__ import annotations

import os
import subprocess
import sys
import tempfile
from collections.abc import Sequence
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from itertools import repeat
from pathlib import Path

from common.repo import ROOT


@dataclass(frozen=True)
class Submodule:
    path: str
    url: str
    # None if .gitmodules sets none, when "git submodule update --remote" follows the remote's HEAD
    branch: str | None

    @property
    def dir(self) -> Path:
        return ROOT / self.path


def git(*args: str, cwd: str | os.PathLike[str] = ROOT) -> subprocess.CompletedProcess[str]:
    return subprocess.run(['git', *args], cwd=cwd, capture_output=True, text=True)


def submodules(rev: str | None = None, paths: Sequence[str] = ()) -> list[Submodule]:
    """The submodules in .gitmodules, as in the working tree or as it was in a revision, limited to some paths."""
    source = ['--blob', f'{rev}:.gitmodules'] if rev else ['-f', '.gitmodules']
    config = git('config', *source, '--get-regexp', r'^submodule\..*\.(path|url|branch)$')
    if config.returncode != 0:
        sys.exit(f'Cannot read .gitmodules{f" in {rev}" if rev else ""}: {config.stderr.strip()}')
    settings: dict[str, dict[str, str]] = {}
    for line in config.stdout.splitlines():
        key, value = line.split(' ', 1)
        name, setting = key.removeprefix('submodule.').rsplit('.', 1)
        settings.setdefault(name, {})[setting] = value
    found = [Submodule(s.get('path', name), s['url'], s.get('branch')) for name, s in settings.items()]

    if not paths:
        return found
    wanted = {os.path.normpath(p) for p in paths}
    unknown = wanted - {s.path for s in found}
    if unknown:
        sys.exit(f'Not submodules: {", ".join(sorted(unknown))}')
    return [s for s in found if s.path in wanted]


def report(found: Sequence[Submodule], statuses: Sequence[str]) -> None:
    width = max(len(s.path) for s in found)
    for s, status in zip(found, statuses):
        print(f'  {s.path.ljust(width)}  {status}')


def initialised(s: Submodule) -> bool:
    return (s.dir / '.git').exists()


# check

def committed(rev: str) -> dict[str, str]:
    """The commit each submodule path points at in a revision."""
    tree = git('ls-tree', '-r', rev)
    if tree.returncode != 0:
        sys.exit(f'Cannot list {rev}: {tree.stderr.strip()}')
    # Lines are "<mode> <type> <object>\t<path>"; submodules are commits (gitlinks)
    return {path: info.split()[2] for info, path in (line.split('\t', 1) for line in tree.stdout.splitlines())
            if info.split()[1] == 'commit'}


def check_one(s: Submodule, commit: str) -> tuple[bool, str]:
    """Whether a submodule's commit is on its tracked branch, and how far behind the branch it is or why not."""
    branch = s.branch or 'HEAD'
    with tempfile.TemporaryDirectory() as repo:
        git('init', '--bare', '--quiet', cwd=repo)
        fetch = git('fetch', '--quiet', '--filter=tree:0', s.url, branch, cwd=repo)
        if fetch.returncode != 0:
            return False, f'cannot fetch {branch} from {s.url}: {fetch.stderr.strip()}'
        # Exits 1 if the commit is not an ancestor, 128 if the fetch did not bring it at all
        if git('merge-base', '--is-ancestor', commit, 'FETCH_HEAD', cwd=repo).returncode != 0:
            return False, f'not on {branch}'
        behind = int(git('rev-list', '--count', f'{commit}..FETCH_HEAD', cwd=repo).stdout)
    return True, f'on {branch}, ' + (f'{behind} commit{"s" if behind != 1 else ""} behind' if behind else 'up to date')


def check(rev: str) -> bool:
    """Check the submodules a revision records, printing how each stands; whether all are on their branches."""
    found = submodules(rev)
    commits = committed(rev)
    for s in found:
        if s.path not in commits:
            sys.exit(f'.gitmodules lists {s.path}, but {rev} has no submodule there')
    with ThreadPoolExecutor() as pool:
        results = list(pool.map(check_one, found, (commits[s.path] for s in found)))

    report(found, [f'{commits[s.path][:12]}  {status}' for s, (_, status) in zip(found, results)])
    failed = [(s, status) for s, (ok, status) in zip(found, results) if not ok]
    if not failed:
        return True
    if os.environ.get('GITHUB_ACTIONS') == 'true':
        for s, problem in failed:
            print(f'::error title=Submodule not on its tracked branch::{s.path} {commits[s.path][:12]} {problem}')
    print(f'\n{len(failed)} submodule(s) not on their tracked branch. Merge their PRs, then run\n'
          f'  scripts/submodule.py update {" ".join(s.path for s, _ in failed)}\n'
          'and commit the new pointers.')
    return False


# update

def update(paths: Sequence[str]) -> int:
    """Merge each tracked branch into the checkout, so local work is kept; a detached HEAD stays detached."""
    found = submodules(paths=paths)
    return subprocess.run(['git', 'submodule', 'update', '--remote', '--merge', '--', *(s.path for s in found)],
                          cwd=ROOT).returncode


# checkout

def checkout_one(s: Submodule) -> tuple[bool, str]:
    if not initialised(s):
        return True, 'not initialised, skipped'
    if not s.branch:
        return True, 'no tracked branch in .gitmodules, skipped'
    # Fetch first, so a branch only on origin can be created to track it
    git('fetch', '--quiet', 'origin', cwd=s.dir)
    if git('symbolic-ref', '--quiet', '--short', 'HEAD', cwd=s.dir).stdout.strip() == s.branch:
        return True, f'already on {s.branch}'
    result = git('switch', '--quiet', s.branch, cwd=s.dir)
    if result.returncode != 0:
        return False, f'cannot switch to {s.branch}: {result.stderr.strip()}'
    return True, f'switched to {s.branch}'


def checkout(paths: Sequence[str]) -> int:
    found = submodules(paths=paths)
    with ThreadPoolExecutor() as pool:
        results = list(pool.map(checkout_one, found))
    report(found, [status for _, status in results])
    return 0 if all(ok for ok, _ in results) else 1


# attach

def refs_at_head(cwd: Path, namespace: str) -> list[str]:
    """Short names of the branches under a ref namespace whose tips are HEAD."""
    refs = git('for-each-ref', '--points-at', 'HEAD', '--format=%(refname)', namespace, cwd=cwd).stdout.split()
    # origin/HEAD is an alias for one of the others, not a branch
    return [r.removeprefix(namespace) for r in refs if r != f'{namespace}HEAD']


def branch_exists(cwd: Path, ref: str) -> bool:
    return git('rev-parse', '--verify', '--quiet', ref, cwd=cwd).returncode == 0


def switch(cwd: Path, branch: str) -> str | None:
    """Switch to a local branch, or create one tracking origin's; why not if that fails."""
    track = [] if branch_exists(cwd, f'refs/heads/{branch}') else ['--track', f'origin/{branch}']
    result = git('switch', '--quiet', *track, *([] if track else [branch]), cwd=cwd)
    return None if result.returncode == 0 else f'cannot switch to {branch}: {result.stderr.strip()}'


def fast_forward(cwd: Path, branch: str) -> tuple[bool, str]:
    """Fast-forward the checked-out branch to origin's, and say how far it moved."""
    upstream = f'refs/remotes/origin/{branch}'
    if not branch_exists(cwd, upstream):
        return True, 'not on origin, not pulled'
    behind = int(git('rev-list', '--count', f'HEAD..{upstream}', cwd=cwd).stdout)
    if not behind:
        return True, 'up to date with origin'
    if git('merge-base', '--is-ancestor', 'HEAD', upstream, cwd=cwd).returncode != 0:
        return False, f'has diverged from origin/{branch}, not pulled; merge or rebase it by hand'
    result = git('merge', '--ff-only', '--quiet', upstream, cwd=cwd)
    if result.returncode != 0:
        # Usually local changes that the new commits would overwrite
        return False, f'cannot fast-forward to origin/{branch}: {" ".join(result.stderr.split())}'
    return True, f'pulled {behind} commit{"s" if behind != 1 else ""}'


def attach_one(s: Submodule, branch: str | None = None, pull: bool = False) -> tuple[bool, str]:
    """Switch a submodule to the preferred branch ending at its commit, so its files are unchanged.
    The given branch wins, then the tracked branch, then the branch already checked out, then a local branch,
    then one on origin.

    With pull, the given branch is switched to wherever it ends, and the branch checked out is then
    fast-forwarded to origin's, so the commit can change."""
    if not initialised(s):
        return True, 'not initialised, skipped'
    fetch = git('fetch', '--quiet', 'origin', cwd=s.dir)
    if fetch.returncode != 0:
        return False, f'cannot fetch origin: {fetch.stderr.strip()}'

    head = git('rev-parse', '--short=12', 'HEAD', cwd=s.dir).stdout.strip()
    current = git('symbolic-ref', '--quiet', '--short', 'HEAD', cwd=s.dir).stdout.strip() or None
    local = refs_at_head(s.dir, 'refs/heads/')
    remote = refs_at_head(s.dir, 'refs/remotes/origin/')
    candidates = list(dict.fromkeys([*local, *remote]))

    if pull and branch and branch != current and (branch_exists(s.dir, f'refs/heads/{branch}')
                                                  or branch_exists(s.dir, f'refs/remotes/origin/{branch}')):
        chosen, others = branch, []
    elif candidates:
        preferred = [b for b in (branch, s.branch, current) if b in candidates]
        chosen = (preferred or candidates)[0]
        others = [b for b in candidates if b != chosen]
        if chosen not in local and chosen != current and branch_exists(s.dir, f'refs/heads/{chosen}') and not pull:
            # A local branch of that name exists but has moved on from origin's: switching would change the files
            return True, f'origin/{chosen} ends at {head}, but local {chosen} does not; skipped'
    else:
        containing = git('branch', '-r', '--contains', 'HEAD', '--format=%(refname:short)', cwd=s.dir).stdout.split()
        containing = [b for b in containing if b not in ('origin/HEAD', 'origin')]
        return True, (f'{head} is not the tip of any branch, left as it is'
                      + (f'; it is on {", ".join(containing)}' if containing else ''))

    if chosen == current:
        status = f'already on {chosen}'
    else:
        error = switch(s.dir, chosen)
        if error:
            return False, error
        status = f'switched to {chosen}'
    ok = True
    if pull:
        ok, pulled = fast_forward(s.dir, chosen)
        status += f', {pulled}'
    now = git('rev-parse', '--short=12', 'HEAD', cwd=s.dir).stdout.strip()
    status += f' at {now}' + (f', was {head}' if now != head else '')
    return ok, status + (f' (also ends here: {", ".join(others)})' if others else '')


def attach(paths: Sequence[str], branch: str | None, pull: bool) -> int:
    found = submodules(paths=paths)
    with ThreadPoolExecutor() as pool:
        results = list(pool.map(attach_one, found, repeat(branch), repeat(pull)))
    report(found, [status for _, status in results])
    return 0 if all(ok for ok, _ in results) else 1


def revisions() -> list[str]:
    """The superproject's branches and tags, for check."""
    return ['HEAD', *git('for-each-ref', '--format=%(refname:short)', 'refs/heads', 'refs/remotes', 'refs/tags').stdout.split()]


def branch_names() -> list[str]:
    """The local and origin branches of the initialised submodules, for attach --branch."""
    names: set[str] = set()
    for s in filter(initialised, submodules()):
        refs = git('for-each-ref', '--format=%(refname:lstrip=2)', 'refs/heads', 'refs/remotes/origin', cwd=s.dir).stdout
        names.update(ref.removeprefix('origin/') for ref in refs.split())
    return sorted(names - {'HEAD'})
