#!/usr/bin/env python3
# pyright: strict
"""Deploy DSF projects and firmware, building them first if asked.

    scripts/deploy.py -H 192.168.4.27 dsf --start-services             every DSF project as last built, to a Pi
    scripts/deploy.py -H 192.168.4.27 DuetControlServer --build --debug build one as Debug, then deploy it
    scripts/deploy.py -H 192.168.4.27 dsf --clean                      rebuild every DSF project from clean
    scripts/deploy.py -H 192.168.4.27 dsf firmware --build             DSF and the firmware of every board, to a Pi
    scripts/deploy.py --local dsf                                        every DSF project, to this machine
    scripts/deploy.py -H duet3.local firmware                            every connected board's firmware
    scripts/deploy.py -H duet3.local firmware --build                    build what the boards need first
    scripts/deploy.py -H duet3.local EXP1HCL --debug                     only the 1HCL boards, as Debug
    scripts/deploy.py -H duet3.local firmware -b 0 -b 121                the main board and CAN address 121
    scripts/deploy.py -H duet3.local firmware --build --dry-run          show what that would do
    scripts/deploy.py -H duet3.local firmware --bootloader               update the expansion boards' bootloaders
    scripts/deploy.py -H duet3.local EXP3HC --bootloader --build         build the 3HCs' bootloader and update theirs
    scripts/deploy.py -H 192.168.4.27 --all --build --dry-run           every DSF project and board, shown only

Targets are DSF projects and firmware boards, and groups of them (see scripts/list.py). With --build,
what is deployed is built first, as scripts/build.py would, and with --clean, from clean; otherwise
what was last built is deployed. Either way it is the build the options name: --debug deploys Debug
builds, and --aot and --arch the DSF projects' builds for those.

DSF projects go to a Pi over SSH, or to this machine with --local: the services are stopped, the
projects' publish directories synced into /opt/dsf/bin and DuetPiManagementPlugin into
/opt/dsf/plugins, and the projects' postinst scripts run. Deploying every DSF project also deletes
binaries the build no longer makes.

Firmware goes to the main board through its HTTP API: DSF's REST API on an SBC, else the rr_ requests
of a standalone board. Its object model says which boards are connected and which firmware file each
needs; those files are uploaded with the main board's IAP, M997 updates the expansion boards one at a
time, and finally the main board. --bootloader updates the expansion boards' bootloaders over CAN
instead (M997 S3); see common/firmware_deploy.py for what it can and cannot update.

Deploying DSF and firmware together deploys DSF first, then starts its services and updates the
firmware through them.
"""

from __future__ import annotations

import sys
from collections.abc import Sequence
from dataclasses import dataclass

from common import firmware_deploy
from common.cli import complete_with, parse, parser, typed
from common.dsf import (TRACY, BuildOptions, DeployOptions, add_build_arguments, build_options, build_projects,
                        deploy, plan)
from common.firmware import load_boards
from common.targets import add_target_arguments, board_names, select


@dataclass(frozen=True)
class Options:
    """The command line, typed."""
    targets: list[str]
    all: bool
    exclude: list[str]
    host: str | None
    local: bool
    debug: bool
    build: bool
    clean: bool
    # DSF
    user: str
    start_services: bool
    dsf: BuildOptions
    # Firmware
    password: str
    addresses: list[int]
    main: str | None
    upload_only: bool
    bootloader: bool
    dry_run: bool


def parse_options(argv: Sequence[str] | None = None) -> Options:
    p = parser(__doc__)
    add_target_arguments(p)
    p.add_argument('-H', '--host', help='the Pi or standalone main board to deploy to: its host name or IP address')
    p.add_argument('-l', '--local', action='store_true', help='deploy to this machine, an SBC')
    p.add_argument('-d', '--debug', action='store_true', help='build, and deploy the firmware of, Debug rather than Release')
    p.add_argument('--build', action='store_true', help='build what is deployed first')
    p.add_argument('-c', '--clean', action='store_true', help='rebuild from clean first (implies --build)')
    p.add_argument('-n', '--dry-run', action='store_true',
                   help='show what would be built, deployed and updated, without doing it')
    dsf = p.add_argument_group('DSF projects')
    dsf.add_argument('-u', '--user', default='root', help='SSH user on the Pi (default: root)')
    dsf.add_argument('--start-services', action='store_true', help='start the DSF services after deploying')
    add_build_arguments(dsf)
    firmware = p.add_argument_group('firmware')
    firmware.add_argument('-P', '--password', default='reprap', help="the main board's password (default: reprap)")
    firmware.add_argument('-b', '--board', dest='addresses', type=int, action='append', default=[],
                          metavar='ADDRESS', help='only update the board at this CAN address (repeatable; the '
                                                  'main board is 0; boards matching a target are updated too)')
    complete_with(firmware.add_argument('-m', '--main', metavar='PRESET',
                                        help="the build to use for the main board's firmware"), board_names)
    firmware.add_argument('--upload-only', action='store_true', help='upload the files but do not update the boards')
    firmware.add_argument('--bootloader', action='store_true',
                          help="update the expansion boards' bootloaders over CAN instead of the boards' firmware "
                               '(see common/firmware_deploy.py)')
    namespace = parse(p, argv)
    return typed(Options, namespace, dsf=build_options(namespace))


def deploy_dsf(options: Options, projects: list[str], start_services: bool) -> int:
    if TRACY in projects:
        sys.exit(f'{TRACY} is not deployed: a -p:Profiling=true build of DuetControlServer carries it')
    build = plan(options.dsf, projects)
    if options.build or options.clean:
        if options.dry_run:
            print(f'Would build {", ".join(build.selected)} ({build.options.variant})'
                  + (' from clean' if options.clean else ''), flush=True)
        elif not build_projects(build):
            return 1
    if not deploy(build, DeployOptions(options.host, options.user, start_services, options.dry_run)):
        return 1
    print('=== DSF dry run complete ===' if options.dry_run else '=== DSF deploy complete ===', flush=True)
    return 0


def deploy_firmware(options: Options, presets: list[str], every: bool, debug: bool, after_dsf: bool) -> int:
    boards = load_boards()
    main = None
    if options.main is not None:
        chosen = select([options.main], kinds=('firmware',))
        if len(chosen.boards) != 1:
            sys.exit(f'--main takes one board, not "{options.main}"')
        main = chosen.preset(chosen.boards[0], debug)
    return firmware_deploy.deploy_firmware(firmware_deploy.Options(
        host=options.host or 'localhost', password=options.password, debug=debug, addresses=options.addresses,
        types=[] if every else presets, main=main, upload_only=options.upload_only, bootloader=options.bootloader,
        build=options.build or options.clean, clean=options.clean, dry_run=options.dry_run, wait=after_dsf and not options.dry_run), boards)


def main() -> int:
    options = parse_options()
    chosen = select(options.targets, options.all, options.exclude)
    if options.local == (options.host is not None):
        sys.exit('Give the machine to deploy to with --host, or --local for this one')
    if chosen.dsf:
        # The firmware is deployed through DSF's own HTTP API, so its services have to be running
        if status := deploy_dsf(options, chosen.dsf, options.start_services or bool(chosen.boards)):
            if chosen.boards:
                print('Not deploying the firmware, as deploying DSF failed', file=sys.stderr)
            return status
    if not chosen.boards:
        return 0
    print(f'=== Deploying firmware through {options.host or "localhost"} ===', flush=True)
    every = len(chosen.boards) == len(load_boards())
    # A board named with -debug deploys the Debug builds, as --debug does: one main board updates the rest
    debug = options.debug or bool(chosen.debug_boards)
    return deploy_firmware(options, [b.preset for b in chosen.boards], every, debug, after_dsf=bool(chosen.dsf))


if __name__ == '__main__':
    sys.exit(main())
