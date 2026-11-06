# pyright: strict
"""Linting a build by hand with clang-tidy: the lint command of scripts/lint.py.

The build lints each translation unit as it compiles it (cmake/ClangTidy.cmake). This runs clang-tidy
over a whole build instead, from its compile database, which is what --fix and the naming-fix export
need. It reports what the build does: the same translation units, and of each one's findings, those in
its own project (see common/clang.py), so every header is judged by its own project's checks.

A firmware board's compile database, which CMake writes when its preset is configured, has to be
rewritten before clang-tidy can use it (see common/clang.py): clang against a database in that state
still reports things, but it is parsing a broken TU, so any --fix output is untrustworthy. It is
reduced to the translation units the build lints, every project's and library's but not third-party
code, and written to build/<preset>/clang-tidy/. DuetRealtimeCore is an ordinary hosted build, whose
own database clang-tidy reads as it is."""

from __future__ import annotations

import json
import re
import shutil
import subprocess
import sys
from collections import Counter
from collections.abc import Iterable, Iterator
from pathlib import Path

from common.clang import cross_compile, own_diagnostics, posix_escape, project_dir, sanitise_database, skipped
from common.repo import BUILD, ROOT, jobs
from common.dsf import REALTIME_CORE

REALTIME_CORE_PRESET = f'{REALTIME_CORE}-native'

# The line run-clang-tidy starts each translation unit's output with: "[3/78][1.5s] clang-tidy ... <file>"
PROGRESS = re.compile(r'^\[\s*\d+/\d+\](\[[\d.]+s\])? .* (?P<source>\S+)$')
DIAGNOSTIC = re.compile(r':[0-9]+:[0-9]+: (warning|error): ')
CHECKS = re.compile(r'\[([^\[\]]+)\]$')
UNNAMED = '(no check name)'


def board_database(preset: str) -> Path:
    """Configure a firmware preset and write its database for clang-tidy; returns the database's directory."""
    # Configuring writes the database for every target; nothing has to be compiled
    subprocess.run(['cmake', '--preset', preset], cwd=ROOT, check=True, stdout=subprocess.DEVNULL)
    directory = BUILD / preset / 'clang-tidy'
    directory.mkdir(exist_ok=True)
    database = directory / 'compile_commands.json'
    shutil.copyfile(BUILD / preset / 'compile_commands.json', database)
    sanitise_database(database, cross_compile())
    return directory


def realtime_core_database() -> Path:
    """Configure DuetRealtimeCore's native preset, whose database clang-tidy can read as it is."""
    subprocess.run(['cmake', '--preset', REALTIME_CORE_PRESET], cwd=ROOT, check=True, stdout=subprocess.DEVNULL)
    return BUILD / REALTIME_CORE_PRESET


def sources(directory: Path) -> list[str]:
    """The translation units in a compile database."""
    entries: list[dict[str, str]] = json.loads((directory / 'compile_commands.json').read_text())
    return list(dict.fromkeys(e['file'] for e in entries))


def plural(n: int, word: str) -> str:
    return word if n == 1 else word + 's'


def own_output(lines: Iterable[str]) -> Iterator[str]:
    """run-clang-tidy's output, each translation unit's reduced to the diagnostics of its own project."""
    source: str | None = None
    block: list[str] = []
    for raw in [*lines, '']:
        line = raw.rstrip('\n')
        if (start := PROGRESS.match(line)) or not raw:
            if source is not None:
                yield from own_diagnostics(block, source)
            source, block = (start['source'] if start else None), []
        elif source is not None:
            block.append(line)
        else:
            yield line  # run-clang-tidy's own messages


def report(lines: Iterable[str]) -> None:
    """Pass the diagnostics through, then tally the checks that fired.

    A single line can name more than one check, comma separated. Such a line counts once towards the
    total and once towards each check it names, so the per-check column can add up to more than the
    total. A finding in a shared header is reported once per translation unit that includes it, so the
    counts are occurrences, not distinct source locations."""
    checks: Counter[str] = Counter()
    warnings = errors = 0
    for line in lines:
        print(line, flush=True)
        if not (diagnostic := DIAGNOSTIC.search(line)):
            continue
        if diagnostic[1] == 'error':
            errors += 1
        else:
            warnings += 1
        if names := CHECKS.search(line):
            for name in (n.strip() for n in names[1].split(',')):
                # Every clang-tidy check is "<group>-<name>"; anything without a dash is a stray
                # bracketed word at the end of a message, not a check
                checks[name if '-' in name else UNNAMED] += 1
        else:
            checks[UNNAMED] += 1

    print('\n== clang-tidy summary ==')
    total = warnings + errors
    if total == 0:
        print('  no diagnostics')
        return
    for name, count in sorted(checks.items(), key=lambda c: (-c[1], c[0])):
        print(f'  {count:6}  {name}')
    print(f'  {"------":>6}  ---')
    print(f'  {total:6}  diagnostics total ({warnings} {plural(warnings, "warning")}, '
          f'{errors} {plural(errors, "error")})')


def tidy(directory: Path, files: list[str], fix: bool) -> None:
    """Run clang-tidy over the files of a compile database the build lints, all of them if none are
    named, and report."""
    if not (directory / 'compile_commands.json').is_file():
        sys.exit(f'There is no compile database in {directory}')
    # The database keeps the TUs the build skips, as the naming-fix export needs every TU
    chosen = [f for f in sources(directory) if not skipped(f) and (not files or f in files)]
    if not chosen:
        sys.exit('None of the files given is linted by the build')
    # One run per project, whose headers alone it checks, so that --fix leaves other projects' alone
    projects: dict[str | None, list[str]] = {}
    for f in chosen:
        projects.setdefault(project_dir(f), []).append(f)
    report(line for project, members in projects.items() for line in run_tidy(directory, project, members, fix))


def run_tidy(directory: Path, project: str | None, files: list[str], fix: bool) -> Iterator[str]:
    """run-clang-tidy over some of a database's files, all in one project, reduced to that project's
    diagnostics."""
    header_filter = [f'-header-filter=^{posix_escape(project)}/'] if project is not None else []
    command = ['run-clang-tidy', '-p', str(directory), '-j', str(jobs()), '-quiet', *header_filter,
               *(['-fix'] if fix else []), *(f'^{re.escape(f)}$' for f in files)]
    print('+', ' '.join(command[:len(command) - len(files)]), f'<{len(files)} files>', flush=True)
    with subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True) as process:
        assert process.stdout is not None
        yield from own_output(process.stdout)


def export_naming_fixes(directory: Path, files: list[str], out: Path, prefix: str) -> int:
    """Export the readability-identifier-naming fixes of some of a database's files into out, as
    <prefix>-<n>.yaml; returns how many were not empty.

    Every one of a project's TUs must be covered, including ones clang reports errors in. A renamed
    declaration in a header is only useful if the use-sites are renamed too, and a use-site's
    replacement is emitted solely by the TU that contains it, so skipping a .cpp renames the header out
    from under it."""
    for i, tu in enumerate(files, 1):
        subprocess.run(['clang-tidy', '-p', str(directory), '--checks=-*,readability-identifier-naming',
                        f'--export-fixes={out / f"{prefix}-{i}.yaml"}', tu],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    # clang-tidy leaves an empty file behind for a clean TU
    for empty in (f for f in out.glob(f'{prefix}-*.yaml') if f.stat().st_size < 2):
        empty.unlink()
    return len(list(out.glob(f'{prefix}-*.yaml')))
