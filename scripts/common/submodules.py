# pyright: strict
"""Checking that every submodule points at a commit on the branch .gitmodules names for it: the
submodules check of scripts/doctor.py. Commits behind the branch pass, and are listed with how far
behind they are. A failure is fixed by merging the submodule's PR, then
"git submodule update --remote <path>" and committing."""

from __future__ import annotations

import os
import subprocess
import sys
import tempfile
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass

from common.repo import ROOT


@dataclass(frozen=True)
class Submodule:
    path: str
    url: str
    branch: str
    commit: str


def git(*args: str, cwd: str | os.PathLike[str] = ROOT) -> subprocess.CompletedProcess[str]:
    return subprocess.run(['git', *args], cwd=cwd, capture_output=True, text=True)


def submodules(rev: str) -> list[Submodule]:
    """The submodules recorded in a revision: .gitmodules as it was then, and the commits it points at."""
    config = git('config', '--blob', f'{rev}:.gitmodules', '--get-regexp', r'^submodule\..*\.(path|url|branch)$')
    if config.returncode != 0:
        sys.exit(f'Cannot read .gitmodules in {rev}: {config.stderr.strip()}')
    settings: dict[str, dict[str, str]] = {}
    for line in config.stdout.splitlines():
        key, value = line.split(' ', 1)
        name, setting = key.removeprefix('submodule.').rsplit('.', 1)
        settings.setdefault(name, {})[setting] = value

    tree = git('ls-tree', '-r', rev)
    if tree.returncode != 0:
        sys.exit(f'Cannot list {rev}: {tree.stderr.strip()}')
    # Lines are "<mode> <type> <object>\t<path>"; submodules are commits (gitlinks)
    commits = {path: info.split()[2] for info, path in (line.split('\t', 1) for line in tree.stdout.splitlines())
               if info.split()[1] == 'commit'}

    found: list[Submodule] = []
    for name, s in settings.items():
        path = s.get('path', name)
        if path not in commits:
            sys.exit(f'.gitmodules lists {path}, but {rev} has no submodule there')
        # With no branch set, "git submodule update --remote" follows the remote's HEAD
        found.append(Submodule(path, s['url'], s.get('branch', 'HEAD'), commits[path]))
    return found


def check(s: Submodule) -> tuple[bool, str]:
    """Whether a submodule's commit is on its tracked branch, and how far behind the branch it is or why not."""
    with tempfile.TemporaryDirectory() as repo:
        git('init', '--bare', '--quiet', cwd=repo)
        fetch = git('fetch', '--quiet', '--filter=tree:0', s.url, s.branch, cwd=repo)
        if fetch.returncode != 0:
            return False, f'cannot fetch {s.branch} from {s.url}: {fetch.stderr.strip()}'
        # Exits 1 if the commit is not an ancestor, 128 if the fetch did not bring it at all
        if git('merge-base', '--is-ancestor', s.commit, 'FETCH_HEAD', cwd=repo).returncode != 0:
            return False, f'not on {s.branch}'
        behind = int(git('rev-list', '--count', f'{s.commit}..FETCH_HEAD', cwd=repo).stdout)
    return True, f'on {s.branch}, ' + (f'{behind} commit{"s" if behind != 1 else ""} behind' if behind else 'up to date')


def check_submodules(rev: str) -> bool:
    """Check the submodules a revision records, printing how each stands; whether all are on their branches."""
    found = submodules(rev)
    with ThreadPoolExecutor() as pool:
        results = list(pool.map(check, found))

    width = max(len(s.path) for s in found)
    for s, (_, status) in zip(found, results):
        print(f'  {s.path.ljust(width)}  {s.commit[:12]}  {status}')
    failed = [(s, status) for s, (ok, status) in zip(found, results) if not ok]
    if not failed:
        return True
    if os.environ.get('GITHUB_ACTIONS') == 'true':
        for s, problem in failed:
            print(f'::error title=Submodule not on its tracked branch::{s.path} {s.commit[:12]} {problem}')
    print(f'\n{len(failed)} submodule(s) not on their tracked branch. Merge their PRs, then run\n'
          f'  git submodule update --remote {" ".join(s.path for s, _ in failed)}\n'
          'and commit the new pointers.')
    return False

