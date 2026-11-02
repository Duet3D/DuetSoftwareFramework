# pyright: strict
"""What the scripts build, deploy, lint and clean: the targets, and choosing them on the command line.

A target is one of
    a DSF project          DuetControlServer, DuetRealtimeCore, ... (common/dsf.py), or TracyClient
    a firmware board       its CMake preset, e.g. EXP3HC or canmaster-Duet3_MB6HC; a -debug suffix
                           names its Debug build, as --debug does for every board
    a group                dsf (every DSF project), firmware (every board), or one firmware project's
                           boards, in full or as canmaster, rrf, d3e, bootloader or iap
Names are not case sensitive."""

from __future__ import annotations

import argparse
import sys
from dataclasses import dataclass, field

from common.dsf import PROJECTS, TRACY
from common.firmware import Board, load_boards

DSF = 'dsf'
FIRMWARE = 'firmware'
ALIASES = {
    'canmaster': 'DuetCANMaster',
    'rrf': 'RepRapFirmware',
    'd3e': 'Duet3Expansion',
    'bootloader': 'Duet3Bootloader',
    'iap': 'DuetIAP',
}
DEBUG = '-debug'


@dataclass(frozen=True)
class Selection:
    """The targets chosen: DSF projects, and firmware boards in the order of CMakePresets.json."""
    dsf: list[str] = field(default_factory=list[str])
    boards: list[Board] = field(default_factory=list[Board])
    # Boards named with the -debug suffix, which build Debug whatever --debug says
    debug_boards: frozenset[str] = frozenset()

    def preset(self, board: Board, debug: bool) -> str:
        """The preset a selected board builds with."""
        return board.variant(debug or board.preset in self.debug_boards)


def groups(boards: list[Board]) -> dict[str, list[str]]:
    """The groups and the target names in each: dsf, firmware, and each firmware project by its name
    and its alias."""
    found: dict[str, list[str]] = {DSF: list(PROJECTS), FIRMWARE: [b.preset for b in boards]}
    for project in dict.fromkeys(b.project for b in boards):
        found[project] = [b.preset for b in boards if b.project == project]
    for alias, project in ALIASES.items():
        if project in found:
            found[alias] = found[project]
    return found


def add_target_arguments(parser: argparse.ArgumentParser, kinds: tuple[str, ...] = (DSF, FIRMWARE),
                         every: bool = True) -> None:
    """The arguments that choose targets: names and groups, --all and --exclude."""
    what = ' or '.join({DSF: 'DSF projects', FIRMWARE: 'firmware boards'}[k] for k in kinds)
    parser.add_argument('targets', nargs='*', metavar='TARGET', help=f'{what}, or groups of them (see scripts/list.py)')
    if every:
        parser.add_argument('-a', '--all', action='store_true', help=f'every one of the {what}')
    parser.add_argument('-x', '--exclude', action='append', default=[], metavar='TARGET',
                        help='leave out a target or a group (repeatable)')


def _resolve(name: str, boards: list[Board], found: dict[str, list[str]]) -> list[str]:
    """The DSF projects and board presets a name stands for."""
    lower = name.lower()
    for group, members in found.items():
        if group.lower() == lower:
            return members
    for target in [*PROJECTS, TRACY, *(b.preset for b in boards)]:
        if target.lower() == lower:
            return [target]
    sys.exit(f'Unknown target "{name}"; run scripts/list.py to see them')


def select(targets: list[str], every: bool = False, exclude: list[str] | None = None,
           kinds: tuple[str, ...] = (DSF, FIRMWARE), required: bool = True) -> Selection:
    """The targets asked for, less the excluded ones, each once."""
    boards = load_boards()
    by_preset = {b.preset: b for b in boards}
    found = groups(boards)
    chosen: list[str] = []
    debug: set[str] = set()
    if every:
        chosen += [*(found[DSF] if DSF in kinds else []), *(found[FIRMWARE] if FIRMWARE in kinds else [])]
    for name in targets:
        base = name[:-len(DEBUG)] if name.lower().endswith(DEBUG) else name
        resolved = _resolve(base, boards, found)
        if base != name:
            if any(r not in by_preset for r in resolved):
                sys.exit(f'"{name}": only firmware boards have Debug builds; DSF projects take --debug')
            debug.update(resolved)
        chosen += resolved
    excluded = {t for name in exclude or [] for t in _resolve(name, boards, found)}
    chosen = [t for t in dict.fromkeys(chosen) if t not in excluded]
    if not chosen and required:
        sys.exit('Name some targets, or use --all; run scripts/list.py to see them')

    dsf = [t for t in chosen if t not in by_preset]
    if dsf and DSF not in kinds:
        sys.exit(f'This takes firmware boards, not DSF projects ({", ".join(dsf)})')
    chosen_boards = [b for b in boards if b.preset in chosen]
    if chosen_boards and FIRMWARE not in kinds:
        sys.exit(f'This takes DSF projects, not firmware boards ({", ".join(b.preset for b in chosen_boards)})')
    return Selection(dsf, chosen_boards, frozenset(debug))
