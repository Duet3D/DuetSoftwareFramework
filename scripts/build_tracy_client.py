#!/usr/bin/env python3
# pyright: strict
"""Build the Tracy client library that a -p:Profiling=true build of DuetControlServer P/Invokes into.

    scripts/build_tracy_client.py                       for this machine, for the system tests
    scripts/build_tracy_client.py --arch linux-arm64    for a Pi

The Tracy-CSharp package ships a prebuilt TracyClient.so, but it is linked against glibc 2.38 and both
the devcontainer and Raspberry Pi OS Bookworm are on 2.36, so it fails to load on either. The package's
managed binding is fine; only the native half is built here, from the Tracy release the binding was
generated against. Client and GUI must be the same release: Tracy's protocol carries a version and the
server refuses a connection from anything else.

The result lands in build/tracy/<arch>/TracyClient.so, which is where DuetControlServer.csproj looks
for it when built with -p:Profiling=true. Build it for the architecture being profiled.
"""

from __future__ import annotations

import argparse
import shutil
import sys
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path

from common.dsf import ELF_MACHINES, ROOT, host_rid, jobs, run_checked, verify_elf_arch

# Must match the Tracy-CSharp version in DuetControlServer.csproj and the Tracy GUI connecting to it
TRACY_VERSION = 'v0.13.1'
TRACY_REPO = 'https://github.com/wolfpld/tracy.git'
SOURCE_DIR = ROOT / 'build' / 'tracy' / 'src'

# The toolchain files live with libduet_realtime_core.so and carry the reasoning about glibc versions
# and sysroots; the same cross compilers produce a client the deployed DuetControlServer can load
TOOLCHAINS = {
    'linux-arm64': ROOT / 'src' / 'DuetRealtimeCore' / 'cmake' / 'aarch64-linux-gnu.cmake',
    'linux-arm': ROOT / 'src' / 'DuetRealtimeCore' / 'cmake' / 'arm-linux-gnueabihf.cmake',
}


@dataclass(frozen=True)
class Options:
    """The command line, typed."""
    arch: str | None
    version: str
    dest_dir: str | None
    on_demand: bool


def parse_options(argv: Sequence[str]) -> Options:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('-a', '--arch', choices=list(ELF_MACHINES),
                        help=f'architecture to build for (default: this machine, {host_rid()})')
    parser.add_argument('-v', '--version', default=TRACY_VERSION, metavar='TAG',
                        help=f'Tracy release to build (default: {TRACY_VERSION})')
    parser.add_argument('-o', '--dest-dir', metavar='DIR',
                        help='where to put the library (default: build/tracy/<arch>)')
    parser.add_argument('--no-on-demand', dest='on_demand', action='store_false',
                        help='record from process start instead of waiting for the GUI to connect')
    return Options(**vars(parser.parse_args(argv)))


def fetch_source(version: str) -> None:
    """A shallow clone of the tag. Re-cloning is the simplest way to move between tags: the checkout is
    disposable build output, not a working tree anyone edits."""
    marker = SOURCE_DIR / '.tracy-version'
    if marker.is_file() and (current := marker.read_text().strip()) != version:
        print(f'    Source tree is {current}, fetching {version} instead')
        shutil.rmtree(SOURCE_DIR)
    if not SOURCE_DIR.is_dir():
        print(f'    Cloning {TRACY_REPO} at {version}')
        run_checked(['git', 'clone', '--depth', '1', '--branch', version, TRACY_REPO, str(SOURCE_DIR)])
        marker.write_text(version + '\n')


def main() -> int:
    options = parse_options(sys.argv[1:])
    host = host_rid()
    arch = options.arch or host
    if arch is None:
        sys.exit('Error: this machine is not an architecture DSF runs on; name one with --arch')
    dest_dir = Path(options.dest_dir).resolve() if options.dest_dir else ROOT / 'build' / 'tracy' / arch
    build_dir = ROOT / 'build' / 'tracy' / arch / 'cmake'

    print(f'=== Tracy client {options.version} for {arch} ===')
    fetch_source(options.version)
    command = ['cmake', '-S', str(SOURCE_DIR), '-B', str(build_dir), '-DCMAKE_BUILD_TYPE=Release',
               '-DBUILD_SHARED_LIBS=ON']

    # On demand the client records nothing until the GUI connects. Without it Tracy starts capturing when
    # DuetControlServer starts and holds every zone in memory until someone connects, which on a
    # long-running service is an unbounded amount of it.
    if options.on_demand:
        command.append('-DTRACY_ON_DEMAND=ON')
        print('    On-demand capture: the client idles until the Tracy GUI connects')
    else:
        print('    Capturing from process start; memory grows until the GUI connects')

    if arch == host:
        if arch in TOOLCHAINS:
            print(f'    Host is already {arch}; building natively')
    elif arch in TOOLCHAINS:
        print(f'    Cross-compiling with {TOOLCHAINS[arch]}')
        command.append(f'-DCMAKE_TOOLCHAIN_FILE={TOOLCHAINS[arch]}')
    else:
        sys.exit(f'Error: cannot build for {arch} on {host}; no cross toolchain')

    run_checked(command)
    run_checked(['cmake', '--build', str(build_dir), '--target', 'TracyClient', f'-j{jobs()}'])
    library = build_dir / 'libTracyClient.so'
    verify_elf_arch(library, arch, build_dir)

    # Named after the package's own native asset, which is the name the managed binding imports
    dest_dir.mkdir(parents=True, exist_ok=True)
    shutil.copy2(library, dest_dir / 'TracyClient.so')
    print(f'=== TracyClient.so -> {dest_dir}/ ===')
    return 0


if __name__ == '__main__':
    sys.exit(main())
