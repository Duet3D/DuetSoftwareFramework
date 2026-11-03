# pyright: strict
"""The firmware boards (CMake presets), building them, and reading the ELF files the builds produce."""

from __future__ import annotations

import bisect
import json
import struct
from dataclasses import dataclass
from pathlib import Path
from typing import NamedTuple, cast

from common.dsf import REALTIME_CORE
from common.repo import ROOT, run

# Boards


@dataclass(frozen=True)
class Board:
    """A board's Release preset."""
    preset: str
    project: str
    display: str
    mcu: str  # the preset that selects its MCU's toolchain, e.g. SAME5x

    def variant(self, debug: bool) -> str:
        """The preset of the board's Release or Debug build."""
        return self.preset + ('-debug' if debug else '')


def load_boards() -> list[Board]:
    """The boards' Release presets, in CMakePresets.json order."""
    document = cast(dict[str, object], json.loads((ROOT / 'CMakePresets.json').read_text()))
    presets = cast(list[dict[str, object]], document['configurePresets'])

    def cache_variables(preset: dict[str, object]) -> dict[str, str]:
        return cast(dict[str, str], preset.get('cacheVariables', {}))

    # DuetRealtimeCore's presets are in the same file, but it is a DSF project (see common/dsf.py)
    projects = {str(p['name']) for p in presets
                if 'DUET_PROJECT' in cache_variables(p) and cache_variables(p)['DUET_PROJECT'] != REALTIME_CORE}
    mcus = {str(p['name']) for p in presets if 'toolchainFile' in p}
    boards: list[Board] = []
    for p in presets:
        if p.get('hidden'):
            continue
        inherits = cast(list[str], p.get('inherits', []))
        project = next((i for i in inherits if i in projects), None)
        if project:
            name = str(p['name'])
            mcu = next((i for i in inherits if i in mcus), '')
            boards.append(Board(name, project, str(p.get('displayName', name)), mcu))
    return boards


def build(preset: str, clean: bool = False, fresh: bool = False, jobs: int | None = None,
          verbose: bool = False) -> bool:
    """Configure and build a preset, after cleaning its build tree if clean is set."""
    configure = ['cmake', '--preset', preset] + (['--fresh'] if fresh else [])
    command = ['cmake', '--build', '--preset', preset]
    if clean:
        command.append('--clean-first')
    if jobs:
        command += ['--parallel', str(jobs)]
    if verbose:
        command.append('--verbose')
    return run(configure) and run(command)


# ELF files

SHT_SYMTAB = 2
SHT_NOBITS = 8
SHF_ALLOC = 2
STT_OBJECT = 1
STT_FUNC = 2


class Segment(NamedTuple):
    """A loaded segment: where it runs, where it is stored, and its size in the file and in memory."""
    vaddr: int
    paddr: int
    filesz: int
    memsz: int


@dataclass(frozen=True)
class Section:
    """An allocated section: one that occupies memory on the target."""
    name: str
    address: int
    size: int
    data: bytes | None  # None if it takes no space in the file (.bss)


@dataclass(frozen=True)
class Symbol:
    name: str
    address: int
    size: int
    function: bool


@dataclass(frozen=True)
class Elf:
    sections: list[Section]
    symbols: list[Symbol]  # functions and objects, by address
    segments: list[Segment]

    def load_address(self) -> int | None:
        """Where the image starts: the lowest address a segment is loaded from."""
        stored = [s.paddr for s in self.segments if s.filesz]
        return min(stored) if stored else None

    def run_address(self, address: int) -> int:
        """The address the bytes stored at an address run from (they differ for initialised data)."""
        for s in self.segments:
            if s.paddr <= address < s.paddr + s.filesz:
                return s.vaddr + address - s.paddr
        return address

    def section_at(self, address: int) -> Section | None:
        return next((s for s in self.sections if s.address <= address < s.address + s.size), None)

    def symbol_at(self, address: int) -> Symbol | None:
        """The symbol spanning an address; the nearest before it may be a short one nested in another."""
        i = bisect.bisect_right([s.address for s in self.symbols], address)
        return next((s for s in reversed(self.symbols[max(0, i - 16):i])
                     if address < s.address + max(s.size, 1)), None)

    def symbol_bytes(self, symbol: Symbol) -> bytes | None:
        section = self.section_at(symbol.address)
        if section is None or section.data is None:
            return None
        start = symbol.address - section.address
        return section.data[start:start + symbol.size]


def read_elf(path: Path) -> Elf:
    """The allocated sections, symbols and loaded segments of a 32-bit little-endian ELF."""
    data = path.read_bytes()
    if data[:6] != b'\x7fELF\x01\x01':
        raise ValueError(f'{path} is not a 32-bit little-endian ELF')
    phoff, shoff = struct.unpack_from('<II', data, 0x1c)
    phentsize, phnum, shentsize, shnum, shstrndx = struct.unpack_from('<5H', data, 0x2a)

    segments: list[Segment] = []
    for i in range(phnum):
        kind, _, vaddr, paddr, filesz, memsz = struct.unpack_from('<6I', data, phoff + i * phentsize)
        if kind == 1:  # PT_LOAD
            segments.append(Segment(vaddr, paddr, filesz, memsz))

    headers = [struct.unpack_from('<10I', data, shoff + i * shentsize) for i in range(shnum)]

    def string(table: int, offset: int) -> str:
        start = headers[table][4] + offset
        return data[start:data.index(b'\0', start)].decode(errors='replace')

    sections: list[Section] = []
    symbols: list[Symbol] = []
    for name, kind, flags, addr, offset, size, link, _, _, entsize in headers:
        if flags & SHF_ALLOC and size:
            contents = None if kind == SHT_NOBITS else data[offset:offset + size]
            sections.append(Section(string(shstrndx, name), addr, size, contents))
        elif kind == SHT_SYMTAB:
            for at in range(offset, offset + size, entsize):
                sym_name, value, sym_size, info = struct.unpack_from('<IIIB', data, at)
                if info & 0xf in (STT_OBJECT, STT_FUNC) and sym_name:
                    # Thumb functions have the low bit of their address set
                    address = value & ~1 if info & 0xf == STT_FUNC else value
                    symbols.append(Symbol(string(link, sym_name), address, sym_size, info & 0xf == STT_FUNC))
    symbols.sort(key=lambda s: (s.address, -s.size))
    return Elf(sections, symbols, segments)
