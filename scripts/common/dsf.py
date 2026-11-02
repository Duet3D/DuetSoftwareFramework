# pyright: strict
"""What the DSF scripts share: the repository, running commands, and the architectures native
libraries are built for."""

from __future__ import annotations

import os
import platform
import subprocess
import sys
from collections.abc import Sequence
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
RESULTS = ROOT / 'test-results'

# The .NET runtime identifiers native libraries are built for, and the e_machine of an ELF built for each
ELF_MACHINES = {
    'linux-arm64': 183,
    'linux-arm': 40,
    'linux-x64': 62,
}
MACHINE_NAMES = {183: 'AArch64', 40: 'ARM', 62: 'X86-64'}
HOST_MACHINES = {
    'aarch64': 'linux-arm64',
    'armv7l': 'linux-arm',
    'armv6l': 'linux-arm',
    'x86_64': 'linux-x64',
}


def call(command: Sequence[str | os.PathLike[str]], cwd: Path | None = ROOT) -> int:
    """Run a command, showing it first, and return its exit status."""
    print('+', ' '.join(map(str, command)), flush=True)
    return subprocess.run(command, cwd=cwd).returncode


def run(command: Sequence[str | os.PathLike[str]], cwd: Path | None = ROOT) -> bool:
    return call(command, cwd) == 0


def run_checked(command: Sequence[str | os.PathLike[str]], cwd: Path | None = ROOT) -> None:
    """Run a command, and exit with its status if it fails."""
    if status := call(command, cwd):
        sys.exit(status)


def jobs() -> int:
    return os.cpu_count() or 1


def host_rid() -> str | None:
    """The runtime identifier of the machine running the build, or None if it is not one DSF runs on."""
    return HOST_MACHINES.get(platform.machine())


def elf_machine(path: Path) -> int | None:
    """The e_machine an ELF file names, or None if the file is not an ELF."""
    with path.open('rb') as f:
        header = f.read(20)
    if len(header) < 20 or header[:4] != b'\x7fELF':
        return None
    return int.from_bytes(header[18:20], 'little' if header[5] == 1 else 'big')


def verify_elf_arch(library: Path, arch: str, build_dir: Path) -> None:
    """Exit if a library was not built for the architecture asked for.

    A build directory first configured without a toolchain file keeps the host compiler forever after:
    the toolchain's set(CMAKE_CXX_COMPILER) is a plain set, which does not override a value already in
    the cache. Pointing a preset at a toolchain afterwards therefore changes nothing, the build still
    succeeds, and it quietly produces host binaries. Nothing notices until the target tries to load
    one, and the loader's message for a shared object of the wrong architecture is "cannot open shared
    object file: No such file or directory" - about a file that is plainly there."""
    expected = ELF_MACHINES.get(arch)
    if expected is None:
        return
    actual = elf_machine(library) if library.is_file() else None
    if actual != expected:
        built = MACHINE_NAMES.get(actual, f'machine {actual}') if actual is not None else 'nothing loadable'
        sys.exit(f"Error: {library.name} was built for '{built}', but {arch} needs '{MACHINE_NAMES[expected]}'.\n"
                 f'       The CMake cache in {build_dir} predates the toolchain file, so it is still\n'
                 '       using the compiler it was first configured with. Build again after:\n'
                 f'           rm -rf {build_dir}')
