#!/usr/bin/env python3
# pyright: strict
"""Build DSF projects, and deploy them to a remote target or locally.

    scripts/build_dsf.py --all                                        build everything into build/dotnet
    scripts/build_dsf.py --all --target 192.168.4.27                  build everything and deploy it
    scripts/build_dsf.py -t 192.168.4.27 DuetControlServer DuetWebServer
    scripts/build_dsf.py --all --local                                build and deploy to this machine
    scripts/build_dsf.py --skip-build --target 192.168.4.27 DuetControlServer
    scripts/build_dsf.py -t 192.168.4.27 --fetch-sysroot DuetRealtimeCore

Projects: DuetControlServer DuetRealtimeCore DuetPiManagementPlugin DuetPluginService DuetWebServer
CodeConsole CodeLogger CodeStream CustomHttpEndpoint ModelObserver PluginManager (or --all).

Projects publish to their own default output directory (bin/<config>/<tfm>/<rid>/publish) and the
selected ones are then collated into the build directory. With --all the whole solution is published
in one go (MSBuild parallelises it); otherwise the selected projects are published concurrently.

DuetRealtimeCore is the native SPI transfer loop (libduet_realtime_core.so) that DuetControlServer
P/Invokes into. DCS cannot run without it, so selecting DuetControlServer builds it too.

DuetPiManagementPlugin is a plugin rather than a service. It is staged into a "plugins" directory
beside the build directory and deployed to /opt/dsf/plugins, which is where DuetPluginService runs it
from; it is not copied in with the binaries. Its manifest is regenerated from src/Directory.Build.props
on every build, because DCS refuses to start a plugin whose manifest names a different DSF version
than the one it is running.

Because a shared library links glibc dynamically, the .so must not need a newer glibc than the target
has. The devcontainer is Debian Bookworm and so targets the same glibc 2.36 as Raspberry Pi OS
Bookworm, meaning a plain cross-build is deployable and no sysroot is involved. To target an older
release, pass --sysroot <dir> or --fetch-sysroot to build against the Pi's own libraries.
"""

from __future__ import annotations

import argparse
import dataclasses
import json
import platform
import shutil
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ElementTree
from collections.abc import Sequence
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from functools import partial
from pathlib import Path
from typing import cast

from common.dsf import ROOT, jobs, run, run_checked, verify_elf_arch

PROJECTS = {
    'DuetControlServer': 'src/DuetControlServer',
    'DuetRealtimeCore': 'src/DuetRealtimeCore',
    'DuetPiManagementPlugin': 'src/DuetPiManagementPlugin',
    'DuetPluginService': 'src/DuetPluginService',
    'DuetWebServer': 'src/DuetWebServer',
    'CodeConsole': 'src/CodeConsole',
    'CodeLogger': 'src/CodeLogger',
    'CodeStream': 'src/CodeStream',
    'CustomHttpEndpoint': 'src/CustomHttpEndpoint',
    'ModelObserver': 'src/ModelObserver',
    'PluginManager': 'src/PluginManager',
}
SOLUTION = ROOT / 'src' / 'DuetSoftwareFramework.sln'
DEFAULT_BUILD_DIR = ROOT / 'build' / 'dotnet'
DEFAULT_AOT_BUILD_DIR = ROOT / 'build' / 'aot'

# The objcopy each architecture's AOT build strips its binaries with
OBJCOPY = {
    'linux-arm': 'arm-linux-gnueabihf-objcopy',
    'linux-arm64': 'aarch64-linux-gnu-objcopy',
    'linux-x64': 'objcopy',
}

# DuetRealtimeCore is native (CMake), not a dotnet project: it builds libduet_realtime_core.so, the SPI
# transfer loop that DuetControlServer P/Invokes into. It is built separately from the rest.
REALTIME_CORE = 'DuetRealtimeCore'
REALTIME_CORE_DIR = ROOT / 'src' / 'DuetRealtimeCore'
REALTIME_CORE_LIB = 'libduet_realtime_core.so'
DEFAULT_SYSROOT = REALTIME_CORE_DIR / 'pi-sysroot'
# Each architecture's cross-compiling preset in src/DuetRealtimeCore/CMakePresets.json, and the host
# machines that are that architecture already. There is no x86_64 cross toolchain in the container, so
# linux-x64 is host-only.
CROSS_PRESETS = {'linux-arm64': 'arm64', 'linux-arm': 'armhf', 'linux-x64': None}
NATIVE_MACHINES = {'linux-arm64': ('aarch64',), 'linux-arm': ('armv7l', 'armv6l'), 'linux-x64': ('x86_64',)}

# DuetPiManagementPlugin is a DSF plugin, not a service: DuetPluginService runs it out of the plugin
# directory, and DuetControlServer only starts it if the manifest beside it declares the DSF version
# that is running. So the plugin is staged into the layout the package installs rather than collated
# into the binary directory with the services, and its manifest is regenerated on every build. The
# project name and the plugin id in the manifest are the same string.
PLUGIN_ID = 'DuetPiManagementPlugin'
PLUGIN_COMMON_DIR = ROOT / 'pkg/common/duetpimanagementplugin/opt/dsf/plugins'
PLUGIN_INSTALL_DIR = '/opt/dsf/plugins'
# Files the packaging marks executable; the plugin runs them by name, so a lost +x bit is a runtime
# failure rather than a build one
PLUGIN_EXECUTABLES = ('gen-https-cert.sh', 'install-dsf.sh')

POSTINST = {
    'DuetControlServer': 'pkg/deb/duetcontrolserver/DEBIAN/postinst',
    'DuetPiManagementPlugin': 'pkg/deb/duetpimanagementplugin/DEBIAN/postinst',
    'DuetPluginService': 'pkg/deb/duetpluginservice/DEBIAN/postinst',
    'DuetWebServer': 'pkg/deb/duetwebserver/DEBIAN/postinst',
}
SERVICES = ['duetcontrolserver', 'duetwebserver', 'duetpluginservice', 'duetpluginservice-root']
# Debug symbols and documentation are no use on the target
DEPLOY_EXCLUDES = ('*.dbg', '*.pdb', '*.xml')


@dataclass(frozen=True)
class Options:
    """The command line, typed."""
    projects: list[str]
    target: str | None
    user: str
    all: bool
    local: bool
    skip_build: bool
    start_services: bool
    sysroot: str | None
    fetch_sysroot: bool
    aot: bool
    arch: str
    build_type: str
    publish_args: str | None
    dest_dir: str | None


@dataclass(frozen=True)
class Build:
    """What the build and the deployment work on."""
    options: Options
    selected: list[str]
    build_dir: Path

    @property
    def plugin_build_dir(self) -> Path:
        # The plugin is staged next to the binaries rather than inside them, so that the binary
        # directory stays a faithful picture of /opt/dsf/bin and can be synced with --delete
        return self.build_dir.parent / 'plugins'

    @property
    def plugin_stage_dir(self) -> Path:
        return self.plugin_build_dir / PLUGIN_ID

    @property
    def plugin_manifest(self) -> Path:
        return self.plugin_build_dir / f'{PLUGIN_ID}.json'

    @property
    def remote(self) -> str:
        return f'{self.options.user}@{self.options.target}'


def parse_options(argv: Sequence[str]) -> Options:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('projects', nargs='*', metavar='PROJECT',
                        help='projects to build (All is the same as --all)')
    parser.add_argument('-t', '--target', metavar='IP', help='target IP address for remote deploy')
    parser.add_argument('-u', '--user', default='root', help='SSH user for the remote target (default: root)')
    parser.add_argument('-a', '--all', action='store_true', help='select every project')
    parser.add_argument('-l', '--local', action='store_true',
                        help='deploy locally to /opt/dsf/bin instead of to a remote target')
    parser.add_argument('--skip-build', action='store_true',
                        help='skip the build; only sync the binaries and run the postinst scripts')
    parser.add_argument('--start-services', action='store_true', help='start the DSF services after deploying')
    parser.add_argument('--sysroot', metavar='DIR',
                        help=f'sysroot to cross-link {REALTIME_CORE_LIB} against. Only needed to target a glibc '
                             "older than the container's 2.36; off by default")
    parser.add_argument('--fetch-sysroot', action='store_true',
                        help='fetch a sysroot from the deploy target and build against it')
    parser.add_argument('--aot', action='store_true', help='build ahead of time binaries')
    parser.add_argument('--arch', default='linux-arm64', choices=list(OBJCOPY),
                        help='architecture to build for (default: linux-arm64)')
    parser.add_argument('--build-type', default='Debug',
                        help='build configuration (default: Debug). Also selects the CMake preset '
                             f'{REALTIME_CORE_LIB} is built with, so Debug builds it unoptimised and steppable')
    parser.add_argument('-p', '--publish-args', metavar='PROPERTY',
                        help='an MSBuild property, without the -p: prefix, e.g. Profiling=true')
    parser.add_argument('-o', '--dest-dir', metavar='DIR',
                        help='where to collate the build (default: build/dotnet, or build/aot/<arch> with --aot)')
    return Options(**vars(parser.parse_args(argv)))


# DuetRealtimeCore


def resolve_sysroot(options: Options) -> Path | None:
    """The sysroot to link against: --sysroot's, one fetched from the target with --fetch-sysroot, or none."""
    if options.sysroot:
        sysroot = Path(options.sysroot)
        if not sysroot.is_dir():
            sys.exit(f"Error: sysroot '{sysroot}' does not exist")
        return sysroot
    if not options.fetch_sysroot:
        return None
    if not options.target:
        sys.exit('Error: --fetch-sysroot needs a deploy target (-t) to fetch from')
    remote = f'{options.user}@{options.target}'
    print(f'=== Fetching sysroot from {remote} (--fetch-sysroot) ===')
    run_checked([REALTIME_CORE_DIR / 'scripts' / 'fetch-pi-sysroot.sh', remote, DEFAULT_SYSROOT])
    return DEFAULT_SYSROOT


def build_realtime_core(build: Build) -> None:
    """Build libduet_realtime_core.so for --arch and put it beside the managed assemblies.

    It is cross-compiled here rather than on the Pi so a deploy needs no toolchain on the target. The
    devcontainer is Debian Bookworm, so its cross toolchain targets the same glibc 2.36 as Raspberry Pi
    OS Bookworm and a plain cross build produces a .so that loads on the Pi. A sysroot is only needed to
    target something older, and is opt-in."""
    options = build.options
    print(f'=== Building {REALTIME_CORE} ({REALTIME_CORE_LIB}) ===')
    machine = platform.machine()
    cross_preset = CROSS_PRESETS[options.arch]
    cmake_args: list[str] = []
    if machine in NATIVE_MACHINES[options.arch]:
        # Already on the target architecture (e.g. building on the Pi itself with --local): that needs
        # no toolchain and no sysroot, and gets glibc right by construction
        print(f'    Host is {machine}; building natively')
        preset = 'native'
    elif cross_preset is None:
        sys.exit(f'Error: cannot build {REALTIME_CORE_LIB} for {options.arch} on {machine}; no cross toolchain')
    elif (sysroot := resolve_sysroot(options)) is not None:
        print(f'    Linking against sysroot: {sysroot}')
        preset = f'{cross_preset}-sysroot'
        # The preset defaults to pi-sysroot/; --sysroot may point somewhere else
        cmake_args.append(f'-DDUET_REALTIME_CORE_SYSROOT={sysroot}')
    else:
        preset = cross_preset
        print(f"    Cross-compiling against the container's {cross_preset} libraries (glibc 2.36)")

    # An optimised .so is not steppable: the debugger loses locals and reorders lines. Every preset has
    # a -debug twin that differs only in CMAKE_BUILD_TYPE, so --build-type picks the same configuration
    # for the native library as it already does for the managed assemblies.
    if options.build_type.lower() == 'debug':
        preset += '-debug'
        print(f'    Build type is {options.build_type}; using the unoptimised preset')

    # --preset must be run from the project directory, where CMakePresets.json is. Only the shared
    # library is built; the harness and the tests are not part of a deployment.
    run_checked(['cmake', '--preset', preset, *cmake_args], cwd=REALTIME_CORE_DIR)
    run_checked(['cmake', '--build', '--preset', preset, '--target', 'duet_realtime_core_shared', f'-j{jobs()}'],
                cwd=REALTIME_CORE_DIR)

    cmake_dir = REALTIME_CORE_DIR / 'build' / preset
    library = cmake_dir / 'src' / REALTIME_CORE_LIB
    verify_elf_arch(library, options.arch, cmake_dir)
    # Beside the managed assemblies, so default P/Invoke probing finds it
    shutil.copy2(library, build.build_dir)
    print(f'=== {REALTIME_CORE_LIB} -> {build.build_dir}/ ===')


# The dotnet projects


def publish_command(options: Options, target: Path) -> list[str]:
    command = ['dotnet', 'publish', str(target), '-r', options.arch, '-c', options.build_type]
    if options.aot:
        command += ['-p:AotPublish=true', f'-p:ObjCopyName={OBJCOPY[options.arch]}']
    else:
        command.append('--self-contained')
    if options.publish_args:
        command.append(f'-p:{options.publish_args}')
    return command


def publish_dir(options: Options, project: str) -> Path:
    """Where a project publishes to. The target framework is part of the path and is not known here."""
    matches = [p for p in (ROOT / PROJECTS[project] / 'bin' / options.build_type).glob(f'*/{options.arch}/publish')
               if p.is_dir()]
    if len(matches) != 1:
        found = ' '.join(map(str, matches))
        sys.exit(f'Error: expected exactly one publish directory for {project}, got: {found}')
    return matches[0]


def publish_concurrently(options: Options, projects: list[str]) -> bool:
    """Publish several projects at once. Each publishes to its own output directory, so they can run
    concurrently; their logs would interleave, so each is captured and only printed if that publish fails."""
    with tempfile.TemporaryDirectory() as logs:
        running: dict[str, subprocess.Popen[bytes]] = {}
        label = 'Native AOT' if options.aot else 'dotnet runtime'
        for project in projects:
            print(f'=== Building {project} ({label}) ===', flush=True)
            with open(Path(logs) / f'{project}.log', 'wb') as log:
                running[project] = subprocess.Popen(publish_command(options, ROOT / PROJECTS[project]),
                                                    stdout=log, stderr=subprocess.STDOUT)
        ok = True
        for project, process in running.items():
            if process.wait() == 0:
                print(f'=== {project} built ===')
            else:
                print(f'Error: {project} build failed', file=sys.stderr)
                sys.stderr.write((Path(logs) / f'{project}.log').read_text(errors='replace'))
                ok = False
        return ok


def collect_output(build: Build) -> None:
    """Merge the per-project publish directories into the build directory. The projects share most of
    their dependencies, so the copies overlap; later copies simply overwrite identical files. The plugin
    is left out: it is staged into the layout it is installed in, and its own copies of the shared
    assemblies must not land in the binary directory."""
    print(f'=== Collecting publish output into {build.build_dir} ===')
    for project in build.selected:
        if project in (REALTIME_CORE, PLUGIN_ID):
            continue
        source = publish_dir(build.options, project)
        shutil.copytree(source, build.build_dir, symlinks=True, dirs_exist_ok=True)
        print(f'    {project} <- {source}')


def dsf_version() -> str:
    """The DSF version the assemblies are given, from src/Directory.Build.props."""
    version = ElementTree.parse(ROOT / 'src' / 'Directory.Build.props').findtext('PropertyGroup/Version')
    if not version:
        sys.exit('Error: could not read the DSF version from src/Directory.Build.props')
    return version


def stage_plugin(build: Build) -> None:
    """Build the plugin directory the way the package does: the publish output and the configuration
    files that ship with it under dsf/, and a manifest beside it listing what ended up there."""
    print(f'=== Staging {PLUGIN_ID} into {build.plugin_build_dir} ===')
    source = publish_dir(build.options, PLUGIN_ID)
    # DCS compares the manifest's sbcDsfVersion against its own version component by component, so the
    # version has to come from the same place the assemblies get theirs
    version = dsf_version()

    # Rebuild from scratch so that a file dropped from the project does not survive in the staged plugin
    # and go on being listed in dsfFiles
    stage = build.plugin_stage_dir
    shutil.rmtree(stage, ignore_errors=True)
    build.plugin_manifest.unlink(missing_ok=True)
    shutil.copytree(source, stage / 'dsf', symlinks=True, ignore=shutil.ignore_patterns(*DEPLOY_EXCLUDES))
    shutil.copytree(PLUGIN_COMMON_DIR / PLUGIN_ID, stage, symlinks=True, dirs_exist_ok=True)
    for executable in PLUGIN_EXECUTABLES:
        path = stage / 'dsf' / executable
        if path.is_file():
            path.chmod(path.stat().st_mode | 0o111)

    # dsfFiles is what an uninstall removes, so it has to name what was actually staged
    manifest = cast(dict[str, object], json.loads((PLUGIN_COMMON_DIR / f'{PLUGIN_ID}.json').read_text()))
    manifest['version'] = version
    manifest['sbcDsfVersion'] = version
    manifest['dsfFiles'] = sorted(p.name for p in (stage / 'dsf').rglob('*') if p.is_file() and not p.is_symlink())
    build.plugin_manifest.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + '\n')

    # Earlier versions of the build collated the plugin in with the services, so a build directory
    # carried over from one of those still holds its binaries and would keep pushing them to
    # /opt/dsf/bin. Only the plugin's own files are dropped; the assemblies it shares with the services
    # belong there on their own account.
    for stale in [build.build_dir / PLUGIN_ID, *build.build_dir.glob(f'{PLUGIN_ID}.*')]:
        if stale.is_file() or stale.is_symlink():
            stale.unlink()
    print(f'    {PLUGIN_ID} {version} <- {source}')


def build_all(build: Build) -> bool:
    options = build.options
    build.build_dir.mkdir(parents=True, exist_ok=True)
    if REALTIME_CORE in build.selected:
        build_realtime_core(build)

    # Nothing is published straight into the build directory: every project publishes to its own and
    # collect_output() copies the selected ones in afterwards, which keeps concurrent publishes from
    # writing to a shared output directory
    label = 'Native AOT' if options.aot else 'dotnet runtime'
    dotnet = [p for p in build.selected if p != REALTIME_CORE]
    if options.all:
        # A single publish of the solution; MSBuild builds the projects in parallel itself. Only the
        # deployable projects publish: the libraries, tests, source generators and documentation are all
        # marked IsPublishable=false, so they are built as dependencies but produce no output here.
        print(f'=== Building solution ({label}) ===')
        if not run(publish_command(options, SOLUTION), cwd=None):
            return False
    elif len(dotnet) == 1:
        print(f'=== Building {dotnet[0]} ({label}) ===')
        if not run(publish_command(options, ROOT / PROJECTS[dotnet[0]]), cwd=None):
            return False
    elif dotnet:
        # The projects share most of their project references (DuetAPI, DuetAPIClient,
        # DuetSharedLibrary). Building one of them first brings those up to date so the parallel
        # publishes do not race each other building the same dependency.
        print('=== Building shared dependencies ===')
        command = ['dotnet', 'build', str(ROOT / PROJECTS[dotnet[0]]), '-r', options.arch,
                   '-c', options.build_type]
        if options.publish_args:
            command.append(f'-p:{options.publish_args}')
        if not run(command, cwd=None) or not publish_concurrently(options, dotnet):
            return False

    if dotnet:
        collect_output(build)
    if PLUGIN_ID in build.selected:
        stage_plugin(build)
    return True


# Deployment


def services(build: Build, action: str) -> None:
    """Stop or restart the DSF services, whether or not they are running."""
    if build.options.local:
        run(['sudo', 'systemctl', action, *SERVICES])
    else:
        run(['ssh', build.remote, f'systemctl {action} {" ".join(SERVICES)} || true'])


def run_postinst(build: Build, project: str) -> bool:
    postinst = ROOT / POSTINST[project]
    print(f'=== Running {project} postinst ===', flush=True)
    if build.options.local:
        return run(['sudo', 'sh', postinst])
    remote = f'/tmp/{project}-postinst'
    ok = (run(['rsync', '-av', postinst, f'{build.remote}:{remote}'])
          and run(['ssh', build.remote, f'chmod +x {remote} && {remote}; rm -f {remote}']))
    if ok:
        print(f'=== {project} postinst completed on {build.options.target} ===')
    return ok


def deploy(build: Build) -> bool:
    options = build.options
    where = 'this machine' if options.local else build.remote
    print(f'=== Stopping DSF services on {where} ===')
    services(build, 'stop')

    # --delete only when every project is selected, to avoid wiping the binaries of unselected ones
    rsync = ['rsync', '-rav', *(f'--exclude={pattern}' for pattern in DEPLOY_EXCLUDES)]
    if options.all:
        rsync.append('--delete')

    # Nothing but the plugin writes to the build directory when the plugin is the only selection, and
    # the directory may still hold an earlier build, so syncing it would push binaries this run never
    # produced
    if any(p != PLUGIN_ID for p in build.selected):
        if options.local:
            print('=== Syncing binaries to /opt/dsf/bin/ ===')
            if not run(['sudo', *rsync, f'{build.build_dir}/', '/opt/dsf/bin/']):
                return False
        else:
            print(f'=== Syncing binaries to {options.target}:/opt/dsf/bin/ ===')
            if not run([*rsync, f'{build.build_dir}/', f'{build.remote}:/opt/dsf/bin/']):
                return False

    # The plugin directory is synced with --delete so a removed file does not linger, but the plugin
    # directory only: other plugins live beside it and must survive a deploy. The manifest is copied
    # separately for the same reason, and because the package makes it read-only, which rsync would
    # otherwise have to work around.
    if PLUGIN_ID in build.selected:
        if not build.plugin_stage_dir.is_dir():
            sys.exit(f'Error: {PLUGIN_ID} has not been staged in {build.plugin_build_dir}; '
                     'run without --skip-build first')
        stage = f'{build.plugin_stage_dir}/'
        if options.local:
            print(f'=== Syncing {PLUGIN_ID} to {PLUGIN_INSTALL_DIR}/ ===')
            ok = (run(['sudo', 'rsync', '-rav', '--delete', stage, f'{PLUGIN_INSTALL_DIR}/{PLUGIN_ID}/'])
                  and run(['sudo', 'install', '-m', '660', build.plugin_manifest,
                           f'{PLUGIN_INSTALL_DIR}/{PLUGIN_ID}.json']))
        else:
            print(f'=== Syncing {PLUGIN_ID} to {options.target}:{PLUGIN_INSTALL_DIR}/ ===')
            ok = (run(['rsync', '-rav', '--delete', stage, f'{build.remote}:{PLUGIN_INSTALL_DIR}/{PLUGIN_ID}/'])
                  and run(['rsync', '-av', '--chmod=F660', build.plugin_manifest,
                           f'{build.remote}:{PLUGIN_INSTALL_DIR}/{PLUGIN_ID}.json']))
        if not ok:
            return False

    # The postinst scripts of the selected projects that have one, in parallel
    with_postinst = [p for p in build.selected if p in POSTINST]
    with ThreadPoolExecutor(max(len(with_postinst), 1)) as pool:
        results = list(zip(with_postinst, pool.map(partial(run_postinst, build), with_postinst)))
    for project, ok in results:
        if not ok:
            print(f'Error: {project} postinst failed', file=sys.stderr)
    if not all(ok for _, ok in results):
        return False

    if options.start_services:
        time.sleep(1)
        print(f'=== Starting DSF services on {where} ===')
        services(build, 'restart')
    return True


def main() -> int:
    options = parse_options(sys.argv[1:])
    print(f'Arch: {options.arch}')
    if options.dest_dir:
        build_dir = Path(options.dest_dir).resolve()
    else:
        build_dir = DEFAULT_AOT_BUILD_DIR / options.arch if options.aot else DEFAULT_BUILD_DIR
    print(f'Build directory: {build_dir}')

    every = options.all or 'All' in options.projects
    if every:
        options = dataclasses.replace(options, all=True)
    selected = list(PROJECTS) if every else list(dict.fromkeys(options.projects))
    if not selected:
        sys.exit('Error: no projects specified. Use --all or list project names.')
    for project in selected:
        if project not in PROJECTS:
            sys.exit(f"Error: unknown project '{project}'")
    # DuetControlServer P/Invokes into libduet_realtime_core.so and will not start without it, so
    # building or deploying DCS always implies the native library too
    if 'DuetControlServer' in selected and REALTIME_CORE not in selected:
        print(f'=== DuetControlServer selected: including {REALTIME_CORE} ({REALTIME_CORE_LIB}) ===')
        selected.append(REALTIME_CORE)
    build = Build(options, selected, build_dir)

    if not options.skip_build and not build_all(build):
        return 1
    if not options.local and not options.target:
        print('=== Build complete. No deployment target specified. ===')
        return 0
    if not deploy(build):
        return 1
    print('=== Deploy complete ===')
    return 0


if __name__ == '__main__':
    sys.exit(main())
