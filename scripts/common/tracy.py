# pyright: strict
"""Building the Tracy client library that a -p:Profiling=true build of DuetControlServer P/Invokes into:
the TracyClient target of scripts/build.py.

The Tracy-CSharp package ships a prebuilt TracyClient.so, but it is linked against glibc 2.38 and both
the devcontainer and Raspberry Pi OS Bookworm are on 2.36, so it fails to load on either. The package's
managed binding is fine; only the native half is built here, from the Tracy release the binding was
generated against. Client and GUI must be the same release: Tracy's protocol carries a version and the
server refuses a connection from anything else.

The result lands in build/tracy/<arch>/TracyClient.so, which is where DuetControlServer.csproj looks
for it when built with -p:Profiling=true. Build it for the architecture being profiled: this machine's
for the system tests, linux-arm64 for a Pi.
"""

from __future__ import annotations

import shutil
import sys

from common.dsf import host_rid, verify_elf_arch
from common.repo import BUILD, ROOT, jobs, run_checked

# Must match the Tracy-CSharp version in DuetControlServer.csproj and the Tracy GUI connecting to it
TRACY_VERSION = 'v0.13.1'
TRACY_REPO = 'https://github.com/wolfpld/tracy.git'
SOURCE_DIR = BUILD / 'tracy' / 'src'

# The toolchain files live with libduet_realtime_core.so and carry the reasoning about glibc versions
# and sysroots; the same cross compilers produce a client the deployed DuetControlServer can load
TOOLCHAINS = {
    'linux-arm64': ROOT / 'src' / 'DuetRealtimeCore' / 'cmake' / 'aarch64-linux-gnu.cmake',
    'linux-arm': ROOT / 'src' / 'DuetRealtimeCore' / 'cmake' / 'arm-linux-gnueabihf.cmake',
}


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


def build_tracy(arch: str | None, version: str = TRACY_VERSION, on_demand: bool = True) -> None:
    """Build the client for an architecture, by default this machine's, into build/tracy/<arch>/."""
    host = host_rid()
    arch = arch or host
    if arch is None:
        sys.exit('Error: this machine is not an architecture DSF runs on; name one with --arch')
    dest_dir = BUILD / 'tracy' / arch
    build_dir = dest_dir / 'cmake'

    print(f'=== Tracy client {version} for {arch} ===')
    fetch_source(version)
    command = ['cmake', '-S', str(SOURCE_DIR), '-B', str(build_dir), '-DCMAKE_BUILD_TYPE=Release',
               '-DBUILD_SHARED_LIBS=ON']

    # On demand the client records nothing until the GUI connects. Without it Tracy starts capturing when
    # DuetControlServer starts and holds every zone in memory until someone connects, which on a
    # long-running service is an unbounded amount of it.
    if on_demand:
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
