# pyright: strict
"""The DSF projects: building them for an SBC and deploying them to one, and the architectures their
native libraries are built for.

The dotnet projects publish to their own default output directories, bin/<config>/<tfm>/<rid>/publish
(bin/aot/... for Native AOT, which src/Directory.Build.props separates), and are deployed from there:
the publish directories of the projects deployed are merged into /opt/dsf/bin in one rsync. Each
configuration, architecture and AOT setting therefore has its own output, and a deploy takes the one
asked for. When every project is selected the whole solution is published in one go (MSBuild
parallelises it); otherwise the selected projects are published concurrently.

DuetRealtimeCore is the native SPI transfer loop (libduet_realtime_core.so) that DuetControlServer
P/Invokes into. DCS cannot run without it, so selecting DuetControlServer builds and deploys it too.
It is built from the root CMakePresets.json, whose DuetRealtimeCore-* presets are its, and deployed from its CMake
build tree, build/<preset>.

DuetPiManagementPlugin is a plugin rather than a service. It is staged into
build/plugins/<arch>-<config>[-aot] in the layout it is installed in, and deployed to /opt/dsf/plugins,
which is where DuetPluginService runs it from; it is not deployed with the binaries. Its manifest is regenerated from src/Directory.Build.props
on every build, because DCS refuses to start a plugin whose manifest names a different DSF version
than the one it is running.

Because a shared library links glibc dynamically, the .so must not need a newer glibc than the target
has. The devcontainer is Debian Bookworm and so targets the same glibc 2.36 as Raspberry Pi OS
Bookworm, meaning a plain cross-build is deployable and no sysroot is involved. To target an older
release, build against the Pi's own libraries with a sysroot."""

from __future__ import annotations

import argparse
import json
import platform
import shutil
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ElementTree
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from functools import partial
from pathlib import Path
from typing import cast

from common.repo import BUILD, ROOT, Command, jobs, run, run_checked

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
PLUGIN_BUILD_DIR = BUILD / 'plugins'
# The DSF assemblies every project carries a copy of, which must be the same build in each project deployed
SHARED_ASSEMBLIES = ('DuetAPI.dll', 'DuetAPIClient.dll', 'DuetSharedLibrary.dll')

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
# Each architecture's cross-compiling preset in the root CMakePresets.json, and the host machines
# that are that architecture already. There is no x86_64 cross toolchain in the container, so
# linux-x64 is host-only.
CROSS_PRESETS = {'linux-arm64': f'{REALTIME_CORE}-arm64', 'linux-arm': f'{REALTIME_CORE}-armhf', 'linux-x64': None}
NATIVE_MACHINES = {'linux-arm64': ('aarch64',), 'linux-arm': ('armv7l', 'armv6l'), 'linux-x64': ('x86_64',)}

# DuetPiManagementPlugin is a DSF plugin, not a service: DuetPluginService runs it out of the plugin
# directory, and DuetControlServer only starts it if the manifest beside it declares the DSF version
# that is running. So the plugin is staged into the layout the package installs rather than deployed
# with the services' binaries, and its manifest is regenerated on every build. The
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

# The Tracy client library a -p:Profiling=true build of DuetControlServer P/Invokes into (common/tracy.py).
# It is a target of its own rather than a project: it is only built when asked for.
TRACY = 'TracyClient'

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


@dataclass(frozen=True)
class BuildOptions:
    """How to build the DSF projects."""
    debug: bool
    aot: bool
    arch: str
    sysroot: str | None
    fetch_sysroot: str | None  # USER@HOST to fetch a sysroot from
    publish_args: str | None
    clean: bool  # clean the selected projects' build output before building them

    @property
    def configuration(self) -> str:
        """The build configuration of the dotnet projects."""
        return 'Debug' if self.debug else 'Release'

    @property
    def variant(self) -> str:
        """The name of this architecture, configuration and AOT setting, for what is staged under build/."""
        return f'{self.arch}-{self.configuration.lower()}' + ('-aot' if self.aot else '')


DEFAULT_ARCH = 'linux-arm64'


def add_build_arguments(group: argparse._ArgumentGroup) -> None:  # pyright: ignore[reportPrivateUsage]
    """The options of a build of the DSF projects, besides --debug and --clean, which every build takes."""
    group.add_argument('--aot', action='store_true', help='build ahead-of-time (Native AOT) binaries')
    group.add_argument('--arch', choices=list(OBJCOPY), help=f'architecture to build for (default: {DEFAULT_ARCH})')
    group.add_argument('--sysroot', metavar='DIR',
                       help=f'sysroot to link {REALTIME_CORE_LIB} against, to target a glibc older than the '
                            "container's 2.36")
    group.add_argument('--fetch-sysroot', metavar='[USER@]HOST',
                       help='fetch a sysroot from a Pi and link against it (the user defaults to root)')
    group.add_argument('-p', '--publish-args', metavar='PROPERTY',
                       help='an MSBuild property, without the -p: prefix, e.g. Profiling=true')


def build_options(namespace: argparse.Namespace) -> BuildOptions:
    """The options add_build_arguments() parsed, and --debug and --clean."""
    values = vars(namespace)
    return BuildOptions(debug=values['debug'], aot=values['aot'], arch=values['arch'] or DEFAULT_ARCH,
                        sysroot=values['sysroot'], fetch_sysroot=values['fetch_sysroot'],
                        publish_args=values['publish_args'], clean=values['clean'])

@dataclass(frozen=True)
class Build:
    """What the build and the deployment work on."""
    options: BuildOptions
    selected: list[str]
    every: bool  # every project is selected

    @property
    def dotnet(self) -> list[str]:
        """The selected dotnet projects."""
        return [p for p in self.selected if p != REALTIME_CORE]

    @property
    def plugin_build_dir(self) -> Path:
        return PLUGIN_BUILD_DIR / self.options.variant

    @property
    def plugin_stage_dir(self) -> Path:
        return self.plugin_build_dir / PLUGIN_ID

    @property
    def plugin_manifest(self) -> Path:
        return self.plugin_build_dir / f'{PLUGIN_ID}.json'


def plan(options: BuildOptions, projects: list[str]) -> Build:
    """The build of some of the projects, with what they cannot run without."""
    selected = list(dict.fromkeys(projects))
    # DuetControlServer P/Invokes into libduet_realtime_core.so and will not start without it, so
    # building or deploying DCS always implies the native library too
    if 'DuetControlServer' in selected and REALTIME_CORE not in selected:
        print(f'=== DuetControlServer selected: including {REALTIME_CORE} ({REALTIME_CORE_LIB}) ===')
        selected.append(REALTIME_CORE)
    return Build(options, selected, every=set(PROJECTS) <= set(selected))


# DuetRealtimeCore


def resolve_sysroot(options: BuildOptions) -> Path | None:
    """The sysroot to link against: --sysroot's, one fetched from the Pi --fetch-sysroot names, or none."""
    if options.sysroot:
        sysroot = Path(options.sysroot)
        if not sysroot.is_dir():
            sys.exit(f"Error: sysroot '{sysroot}' does not exist")
        return sysroot
    if not options.fetch_sysroot:
        return None
    remote = options.fetch_sysroot if '@' in options.fetch_sysroot else f'root@{options.fetch_sysroot}'
    print(f'=== Fetching sysroot from {remote} (--fetch-sysroot) ===')
    run_checked([REALTIME_CORE_DIR / 'scripts' / 'fetch-pi-sysroot.sh', remote, DEFAULT_SYSROOT])
    return DEFAULT_SYSROOT


def realtime_core_preset(options: BuildOptions) -> str:
    """The preset in the root CMakePresets.json that builds the library for these options.

    Already on the target architecture (e.g. building on the Pi itself with --local) needs no
    toolchain and no sysroot, and gets glibc right by construction. Otherwise the library is
    cross-compiled, against a sysroot if one is given. Every preset has a -debug twin that differs only
    in CMAKE_BUILD_TYPE, so --debug picks the same configuration for the native library as it does for
    the managed assemblies: an optimised .so is not steppable."""
    machine = platform.machine()
    cross_preset = CROSS_PRESETS[options.arch]
    if machine in NATIVE_MACHINES[options.arch]:
        preset = f'{REALTIME_CORE}-native'
    elif cross_preset is None:
        sys.exit(f'Error: cannot build {REALTIME_CORE_LIB} for {options.arch} on {machine}; no cross toolchain')
    elif options.sysroot or options.fetch_sysroot:
        preset = f'{cross_preset}-sysroot'
    else:
        preset = cross_preset
    return preset + '-debug' if options.debug else preset


def realtime_core_library(options: BuildOptions) -> Path:
    """Where the build for these options puts libduet_realtime_core.so."""
    return BUILD / realtime_core_preset(options) / REALTIME_CORE_LIB


def build_realtime_core(options: BuildOptions) -> None:
    """Build libduet_realtime_core.so for --arch.

    It is cross-compiled here rather than on the Pi so a deploy needs no toolchain on the target. The
    devcontainer is Debian Bookworm, so its cross toolchain targets the same glibc 2.36 as Raspberry Pi
    OS Bookworm and a plain cross build produces a .so that loads on the Pi. A sysroot is only needed to
    target something older, and is opt-in."""
    preset = realtime_core_preset(options)
    print(f'=== Building {REALTIME_CORE} ({REALTIME_CORE_LIB}, preset {preset}) ===')
    cmake_args: list[str] = []
    if '-sysroot' in preset and (sysroot := resolve_sysroot(options)) is not None:
        print(f'    Linking against sysroot: {sysroot}')
        # The preset defaults to pi-sysroot/; --sysroot may point somewhere else
        cmake_args.append(f'-DDUET_REALTIME_CORE_SYSROOT={sysroot}')

    # Only the shared library is built; the harness and the tests are not part of a deployment
    run_checked(['cmake', '--preset', preset, *cmake_args])
    run_checked(['cmake', '--build', '--preset', preset, '--target', 'duet_realtime_core_shared', f'-j{jobs()}',
                 *(['--clean-first'] if options.clean else [])])
    library = realtime_core_library(options)
    verify_elf_arch(library, options.arch, library.parent)


# The dotnet projects


def properties(options: BuildOptions) -> list[str]:
    """The MSBuild properties every dotnet command of a build is given, so that they agree on the output."""
    command = ['-r', options.arch, '-c', options.configuration]
    if options.aot:
        command += ['-p:AotPublish=true', f'-p:ObjCopyName={OBJCOPY[options.arch]}']
    if options.publish_args:
        command.append(f'-p:{options.publish_args}')
    return command


def publish_command(options: BuildOptions, target: Path) -> list[str]:
    return ['dotnet', 'publish', str(target), *properties(options), *([] if options.aot else ['--self-contained'])]


def publish_dirs(options: BuildOptions, project: str) -> list[Path]:
    """The directories a project has published to with these options, one per target framework."""
    # src/Directory.Build.props puts the Native AOT output under bin/aot
    output = ROOT / PROJECTS[project] / 'bin' / ('aot' if options.aot else '') / options.configuration
    return sorted(p for p in output.glob(f'*/{options.arch}/publish') if p.is_dir())


def publish_dir(options: BuildOptions, project: str, missing_ok: bool = False) -> Path:
    """Where a project publishes to. The target framework is part of the path and is not known here, so
    with missing_ok, a project not built yet is given a placeholder for it."""
    matches = publish_dirs(options, project)
    if not matches and missing_ok:
        return ROOT / PROJECTS[project] / 'bin' / ('aot' if options.aot else '') / options.configuration / \
            '<tfm>' / options.arch / 'publish'
    if not matches:
        sys.exit(f'Error: {project} has not been built for {options.variant}; deploy with --build')
    if len(matches) != 1:
        sys.exit(f'Error: expected one publish directory for {project}, got: {" ".join(map(str, matches))}')
    return matches[0]


def clean_dotnet(build: Build) -> bool:
    """Clean the selected dotnet projects and the projects they reference, for these options, and delete
    their publish directories, which the clean leaves behind."""
    options = build.options
    targets = [SOLUTION] if build.every else [ROOT / PROJECTS[p] for p in build.dotnet]
    for target in targets:
        print(f'=== Cleaning {target.stem} ===', flush=True)
        if not run(['dotnet', 'clean', str(target), *properties(options), '-v', 'q'], cwd=None):
            return False
    for project in build.dotnet:
        for directory in publish_dirs(options, project):
            shutil.rmtree(directory)
    return True


def publish_concurrently(options: BuildOptions, projects: list[str]) -> bool:
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
    print(f'    {PLUGIN_ID} {version} <- {source}')


def build_projects(build: Build) -> bool:
    """Build the selected projects, each into its own output directory."""
    options = build.options
    if REALTIME_CORE in build.selected:
        build_realtime_core(options)

    label = 'Native AOT' if options.aot else 'dotnet runtime'
    dotnet = build.dotnet
    if dotnet and options.clean and not clean_dotnet(build):
        return False
    if build.every:
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
        command = ['dotnet', 'build', str(ROOT / PROJECTS[dotnet[0]]), *properties(options)]
        if not run(command, cwd=None) or not publish_concurrently(options, dotnet):
            return False

    if PLUGIN_ID in build.selected:
        stage_plugin(build)
    return True


# Deployment


@dataclass(frozen=True)
class DeployOptions:
    """Where to deploy the DSF projects: a Pi over SSH, or this machine."""
    host: str | None
    user: str
    start_services: bool
    dry_run: bool = False  # show the commands instead of running them

    @property
    def local(self) -> bool:
        return self.host is None

    @property
    def remote(self) -> str:
        return f'{self.user}@{self.host}'

    def run(self, command: Command) -> bool:
        """Run a command of the deployment, or with dry_run, show it."""
        if self.dry_run:
            print('Would run:', ' '.join(map(str, command)), flush=True)
            return True
        return run(command)


def services(deploy: DeployOptions, action: str) -> None:
    """Stop or restart the DSF services, whether or not they are running."""
    if deploy.local:
        deploy.run(['sudo', 'systemctl', action, *SERVICES])
    else:
        deploy.run(['ssh', deploy.remote, f'systemctl {action} {" ".join(SERVICES)} || true'])


def write_dcs_pid(deploy: DeployOptions) -> None:
    """Write the process ID of DuetControlServer on the target to .vscode/dcs.pid, from where the
    "[DSF] Attach Native (lib*.so)" launch configuration reads it. Empty if DCS is not running."""
    command = ['pidof', 'DuetControlServer']
    if not deploy.local:
        command = ['ssh', '-T', deploy.remote, ' '.join(command)]
    if deploy.dry_run:
        print('Would run:', ' '.join(command), '> .vscode/dcs.pid', flush=True)
        return
    pid = next(iter(subprocess.run(command, capture_output=True, text=True).stdout.split()), '')
    (ROOT / '.vscode' / 'dcs.pid').write_text(pid + '\n')
    print(f'DuetControlServer PID: {pid or "not running"} (.vscode/dcs.pid)', flush=True)


def run_postinst(deploy: DeployOptions, project: str) -> bool:
    postinst = ROOT / POSTINST[project]
    print(f'=== Running {project} postinst ===', flush=True)
    if deploy.local:
        return deploy.run(['sudo', 'sh', postinst])
    remote = f'/tmp/{project}-postinst'
    ok = (deploy.run(['rsync', '-av', postinst, f'{deploy.remote}:{remote}'])
          and deploy.run(['ssh', deploy.remote, f'chmod +x {remote} && {remote}; rm -f {remote}']))
    if ok and not deploy.dry_run:
        print(f'=== {project} postinst completed on {deploy.host} ===')
    return ok


def binary_sources(build: Build, missing_ok: bool = False) -> list[Path]:
    """The directories and files that make up /opt/dsf/bin for the selected projects: the services' and
    tools' publish directories, and DuetRealtimeCore's library. The plugin is installed elsewhere. With
    missing_ok, as in a dry run of a build and deploy, what has not been built yet is not an error."""
    sources = [publish_dir(build.options, p, missing_ok) for p in build.dotnet if p != PLUGIN_ID]
    if REALTIME_CORE in build.selected:
        library = realtime_core_library(build.options)
        if not library.is_file() and not missing_ok:
            sys.exit(f'Error: {REALTIME_CORE} has not been built for {build.options.variant} ({library}); '
                     'deploy with --build')
        sources.append(library)
    return sources


def check_shared_assemblies(sources: list[Path]) -> None:
    """Warn when the publish directories deployed together hold different builds of the DSF assemblies
    they share. rsync takes each file from the first source that has it, so the projects would end up
    running against an assembly they were not built with."""
    for name in SHARED_ASSEMBLIES:
        copies = {s: (s / name).read_bytes() for s in sources if (s / name).is_file()}
        if len(set(copies.values())) > 1:
            print(f'Warning: {name} differs between the projects deployed; build them together '
                  f'(deploy with --build) so that they share one ({", ".join(map(str, copies))})',
                  file=sys.stderr)


def deploy(build: Build, to: DeployOptions) -> bool:
    """Install what a build produced on the Pi or on this machine, with the services stopped."""
    sources = binary_sources(build, to.dry_run)
    check_shared_assemblies(sources)
    if PLUGIN_ID in build.selected and not build.plugin_stage_dir.is_dir() and not to.dry_run:
        sys.exit(f'Error: {PLUGIN_ID} has not been staged in {build.plugin_build_dir}; deploy with --build first')

    where = 'this machine' if to.local else to.remote
    print(f'=== Stopping DSF services on {where} ===')
    services(to, 'stop')

    # The publish directories overlap, sharing the runtime and the DSF assemblies, and rsync merges them,
    # taking each file from the first that has it. --delete removes what none of them has, so only when
    # every project is selected, to keep the binaries of the unselected ones.
    rsync = ['rsync', '-rav', *(f'--exclude={pattern}' for pattern in DEPLOY_EXCLUDES)]
    if build.every:
        rsync.append('--delete')
    # The publish directories' contents, not the directories themselves
    paths = [str(s) if s.name == REALTIME_CORE_LIB else f'{s}/' for s in sources]
    if sources:
        if to.local:
            print('=== Syncing binaries to /opt/dsf/bin/ ===')
            if not to.run(['sudo', *rsync, *paths, '/opt/dsf/bin/']):
                return False
        else:
            print(f'=== Syncing binaries to {to.host}:/opt/dsf/bin/ ===')
            if not to.run([*rsync, *paths, f'{to.remote}:/opt/dsf/bin/']):
                return False

    # The plugin directory is synced with --delete so a removed file does not linger, but the plugin
    # directory only: other plugins live beside it and must survive a deploy. The manifest is copied
    # separately for the same reason, and because the package makes it read-only, which rsync would
    # otherwise have to work around.
    if PLUGIN_ID in build.selected:
        stage = f'{build.plugin_stage_dir}/'
        if to.local:
            print(f'=== Syncing {PLUGIN_ID} to {PLUGIN_INSTALL_DIR}/ ===')
            ok = (to.run(['sudo', 'rsync', '-rav', '--delete', stage, f'{PLUGIN_INSTALL_DIR}/{PLUGIN_ID}/'])
                  and to.run(['sudo', 'install', '-m', '660', build.plugin_manifest,
                           f'{PLUGIN_INSTALL_DIR}/{PLUGIN_ID}.json']))
        else:
            print(f'=== Syncing {PLUGIN_ID} to {to.host}:{PLUGIN_INSTALL_DIR}/ ===')
            ok = (to.run(['rsync', '-rav', '--delete', stage, f'{to.remote}:{PLUGIN_INSTALL_DIR}/{PLUGIN_ID}/'])
                  and to.run(['rsync', '-av', '--chmod=F660', build.plugin_manifest,
                           f'{to.remote}:{PLUGIN_INSTALL_DIR}/{PLUGIN_ID}.json']))
        if not ok:
            return False

    # The postinst scripts of the selected projects that have one, in parallel, unless only shown
    with_postinst = [p for p in build.selected if p in POSTINST]
    with ThreadPoolExecutor(1 if to.dry_run else max(len(with_postinst), 1)) as pool:
        results = list(zip(with_postinst, pool.map(partial(run_postinst, to), with_postinst)))
    for project, ok in results:
        if not ok:
            print(f'Error: {project} postinst failed', file=sys.stderr)
    if not all(ok for _, ok in results):
        return False

    if to.start_services:
        time.sleep(0 if to.dry_run else 1)
        print(f'=== Starting DSF services on {where} ===')
        services(to, 'restart')
        write_dcs_pid(to)
    return True
