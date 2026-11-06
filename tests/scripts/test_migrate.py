# pyright: strict
"""migrate.py against a fake Pi, which records what is done to it in order. The order is the point: the
main board has to be flashed by the stack that is leaving, before DSF is replaced."""

from __future__ import annotations

import subprocess
import time
from collections.abc import Sequence
from unittest.mock import patch

import migrate
from common import dsf, firmware_deploy
from common.dsf import Build, DeployOptions
from common.duet import Connector, DuetError, TransportError
from common.repo import Command

from .support import ScriptTest, run_main

PACKAGES = ('duetruntime 3.7.0 ii \nduetcontrolserver 3.7.0 ii \nduettools 3.7.0 ii \nduetwebserver 3.7.0 ii \n'
            'duetpluginservice 3.7.0 ii \nduetpimanagementplugin 3.7.0 ii \nduetsd 1.1.0 ii \nreprapfirmware 3.7.0-5 ii \n')


class FakePi(Connector):
    """A Pi's REST API. M997 flashes the main board and replacing DSF changes the version it reports."""
    mode = 'SBC'
    sbc = True

    def __init__(self, version: str, firmware: str) -> None:
        super().__init__('http://pi', None, 1, 1)
        self.version = version
        self.firmware = firmware
        self.status = 'idle'
        self.reply = ''
        self.unreachable = False
        self.refused = False
        self.session = True
        self.actions: list[str] = []

    def connect(self) -> None:
        self.session = True

    def code(self, gcode: str, wait: bool = True) -> str: return self.send(gcode)
    def replies(self) -> str: return ''
    def filelist(self, directory: str) -> list[dict[str, object]]: return []
    def fileinfo(self, name: str) -> object: return None
    def download(self, name: str) -> bytes: return b''
    def delete(self, name: str, recursive: bool) -> None: pass
    def move(self, old: str, new: str, force: bool) -> None: pass
    def mkdir(self, directory: str) -> None: pass

    def send(self, gcode: str) -> str:
        self.actions.append(gcode)
        if self.unreachable:
            raise TransportError('POST /machine/code timed out after 180s')
        if self.refused or not self.session:
            raise TransportError('POST /machine/code: HTTP 401 Unauthorized')
        if 'Error' not in self.reply:
            self.firmware = gcode.split('"')[1]
        return self.reply

    def model(self, key: str, flags: str | None = None) -> object:
        if key == 'sbc.dsf.version':
            return self.version
        if key == 'state':
            return {'status': self.status, 'upTime': 100}
        return [{'canAddress': 0, 'shortName': 'MB6HC', 'firmwareFileName': self.firmware}]

    def upload(self, name: str, data: bytes, modified: str | None = None) -> None:
        self.actions.append(f'upload {name}')


class MigrateTest(ScriptTest):
    def setUp(self) -> None:
        super().setUp()
        self.enterContext(patch.object(time, 'sleep'))
        self.enterContext(patch.object(migrate, 'dsf_version', return_value='4.0.0-alpha.1'))
        self.enterContext(patch.object(migrate, 'binary_sources', return_value=[]))
        self.enterContext(patch.object(Build, 'plugin_stage_dir', self.root))
        self.enterContext(patch.object(subprocess, 'run', side_effect=self.ssh_output))
        self.enterContext(patch.object(dsf, 'run', side_effect=self.ssh))
        self.enterContext(patch.object(migrate, 'deploy', side_effect=self.deploy))
        self.build_preset = self.enterContext(patch.object(migrate, 'build', return_value=True))
        self.build_dsf = self.enterContext(patch.object(migrate, 'build_projects', return_value=True))
        self.failing = ''  # what a command must contain to fail
        self.packages = PACKAGES
        self.pi = FakePi('3.7.0', 'Duet3Firmware_MB6HC.bin')
        self.open_session = self.enterContext(patch.object(firmware_deploy, 'open_session', return_value=self.pi))
        self.built('DuetCANMaster_MB6HC', 'DuetCANMaster_MB6HC.bin')
        self.built('Duet3_SBCiap32_MB6HC', 'Duet3_SBCiap32_MB6HC.bin')

    def ssh_output(self, command: Sequence[str], **_: object) -> subprocess.CompletedProcess[str]:
        return subprocess.CompletedProcess(command, 0, stdout=self.packages if 'dpkg-query' in command[-1] else '')

    def ssh(self, command: Command) -> bool:
        line = str(command[-1])
        self.pi.actions.append(line)
        self.pi.session = False  # anything done over SSH can outlast the web server's session timeout
        if 'apt-get install' in line and '--download-only' not in line:
            self.pi.version = '3.7.0'
        return not self.failing or self.failing not in line

    def deploy(self, build: Build, to: DeployOptions) -> bool:
        self.pi.actions.append('deploy DSF')
        assert to.start_services
        if not to.dry_run:
            self.pi.version = '4.0.0-alpha.1+a85e7147'
        return not self.failing

    def migrate(self, direction: str, *argv: str) -> tuple[int, str]:
        return run_main(migrate.main, direction, '-H', 'pi', *argv)

    def on_dsf4(self) -> None:
        self.pi.version = '4.0.0-alpha.1+a85e7147'
        self.pi.firmware = 'DuetCANMaster_MB6HC.bin'

    def index(self, text: str) -> int:
        return next(i for i, action in enumerate(self.pi.actions) if text in action)

    def has(self, text: str) -> bool:
        return any(text in action for action in self.pi.actions)


class Upgrade(MigrateTest):
    def test_flashes_the_main_board_through_dsf3_before_deploying_dsf4(self) -> None:
        status, out = self.migrate('upgrade')
        self.assertEqual(status, 0, out)
        self.assertEqual(self.pi.actions, ['upload 0:/firmware/DuetCANMaster_MB6HC.bin',
                                           'upload 0:/firmware/Duet3_SBCiap32_MB6HC.bin',
                                           'M997 P"DuetCANMaster_MB6HC.bin"', 'deploy DSF'])
        self.assertIn('is on DSF 4 with DuetCANMaster', out)

    def test_a_refused_flash_leaves_dsf3_in_place(self) -> None:
        self.pi.reply = 'Error: Failed to find IAP file'
        status, out = self.migrate('upgrade')
        self.assertEqual(status, 1)
        self.assertIn('Failed to find IAP file', out)
        self.assertFalse(self.has('deploy DSF'))

    def test_an_unanswered_flash_is_not_taken_for_a_flash(self) -> None:
        self.pi.unreachable = True
        status, out = self.migrate('upgrade')
        self.assertEqual(status, 1)
        self.assertIn('--dsf-only', out)
        self.assertFalse(self.has('deploy DSF'))

    def test_dsf4_not_built_stops_it_before_the_main_board_is_touched(self) -> None:
        with patch.object(migrate, 'binary_sources', side_effect=SystemExit('not built')):
            status, out = self.migrate('upgrade')
        self.assertEqual(status, 1)
        self.assertIn('not built', out)
        self.assertEqual(self.pi.actions, [])

    def test_duetcanmaster_not_built_stops_it_before_the_main_board_is_touched(self) -> None:
        (self.build / 'DuetCANMaster_MB6HC/DuetCANMaster_MB6HC.bin').unlink()
        status, out = self.migrate('upgrade')
        self.assertEqual(status, 1)
        self.assertIn('DuetCANMaster_MB6HC has not been built', out)
        self.assertEqual(self.pi.actions, [])

    def test_builds_first_when_asked(self) -> None:
        status, out = self.migrate('upgrade', '--build')
        self.assertEqual(status, 0, out)
        self.assertEqual([c.args[0] for c in self.build_preset.call_args_list],
                         ['DuetCANMaster_MB6HC', 'Duet3_SBCiap32_MB6HC'])
        self.build_dsf.assert_called_once()

    def test_a_failed_build_touches_nothing(self) -> None:
        self.build_dsf.return_value = False
        status, _ = self.migrate('upgrade', '--build')
        self.assertEqual(status, 1)
        self.assertEqual(self.pi.actions, [])

    def test_the_debug_build_is_uploaded_under_the_name_the_iap_expects(self) -> None:
        self.built('DuetCANMaster_MB6HC-debug', 'DuetCANMaster_MB6HC.bin')
        status, out = self.migrate('upgrade', '--debug')
        self.assertEqual(status, 0, out)
        self.assertIn('Uploading DuetCANMaster_MB6HC-debug to 0:/firmware/DuetCANMaster_MB6HC.bin', out)
        self.assertTrue(self.has('M997 P"DuetCANMaster_MB6HC.bin"'))

    def test_a_board_duetcanmaster_is_not_built_for_is_refused(self) -> None:
        self.pi.firmware = 'Duet3Firmware_Mini5plus.bin'
        with patch.object(FakePi, 'model', self.mini):
            status, out = self.migrate('upgrade')
        self.assertEqual(status, 1)
        self.assertIn('not built for a Mini5plus', out)
        self.assertEqual(self.pi.actions, [])

    def mini(self, key: str, flags: str | None = None) -> object:
        if key == 'boards':
            return [{'canAddress': 0, 'shortName': 'Mini5plus', 'firmwareFileName': 'Duet3Firmware_Mini5plus.bin'}]
        return {'status': 'idle', 'upTime': 100} if key == 'state' else '3.7.0'

    def test_dsf_only_finishes_a_flashed_board_without_talking_to_it_first(self) -> None:
        self.pi.firmware = 'DuetCANMaster_MB6HC.bin'
        status, out = self.migrate('upgrade', '--dsf-only')
        self.assertEqual(status, 0, out)
        self.assertEqual(self.pi.actions, ['deploy DSF'])

    def test_a_failed_deploy_says_how_to_finish(self) -> None:
        self.failing = 'x'
        status, out = self.migrate('upgrade')
        self.assertEqual(status, 1)
        self.assertIn('--dsf-only', out)

    def test_reports_a_machine_that_does_not_come_up(self) -> None:
        with patch.object(migrate, 'VERIFY_TIMEOUT', 0):
            status, out = self.migrate('upgrade')
        self.assertEqual(status, 1)
        self.assertIn('has not come up', out)


class Downgrade(MigrateTest):
    def setUp(self) -> None:
        super().setUp()
        self.on_dsf4()

    def test_flashes_the_main_board_through_dsf4_before_reinstalling_dsf3(self) -> None:
        status, out = self.migrate('downgrade')
        self.assertEqual(status, 0, out)
        steps = [self.index('--download-only'), self.index('dpkg-deb'), self.index('M997 P"Duet3Firmware_MB6HC.bin"'),
                 self.index('systemctl stop'), self.index('--force-confold'), self.index('comm -23'),
                 self.index('systemctl restart')]
        self.assertEqual(steps, sorted(steps))
        self.assertIn('is on DSF 3 with RepRapFirmware', out)

    def test_a_session_that_expires_while_preparing_does_not_stop_the_flash(self) -> None:
        status, out = self.migrate('downgrade')
        self.assertEqual(status, 0, out)
        self.assertEqual(self.pi.firmware, 'Duet3Firmware_MB6HC.bin')

    def test_a_refused_code_is_reported_as_not_flashed(self) -> None:
        self.pi.refused = True
        status, out = self.migrate('downgrade')
        self.assertEqual(status, 1)
        self.assertIn('has not been flashed', out)
        self.assertNotIn('--dsf-only', out)
        self.assertFalse(self.has('--force-confold'))

    def test_firmware_comes_from_the_installed_version_of_the_package(self) -> None:
        self.migrate('downgrade')
        self.assertTrue(self.has('apt-get download reprapfirmware=3.7.0-5'))

    def test_reinstalls_at_the_installed_versions_without_the_firmware_package(self) -> None:
        self.migrate('downgrade')
        reinstall = self.pi.actions[self.index('--force-confold')]
        self.assertIn('duetcontrolserver=3.7.0', reinstall)
        self.assertNotIn('reprapfirmware', reinstall)
        self.assertNotIn('duetsd', reinstall)

    def test_packages_that_cannot_be_downloaded_stop_it_before_the_flash(self) -> None:
        self.failing = '--download-only'
        status, out = self.migrate('downgrade')
        self.assertEqual(status, 1)
        self.assertIn('cannot be downloaded', out)
        self.assertFalse(self.has('M997'))

    def test_firmware_that_cannot_be_unpacked_stops_it_before_the_flash(self) -> None:
        self.failing = 'dpkg-deb'
        status, out = self.migrate('downgrade')
        self.assertEqual(status, 1)
        self.assertIn('cannot be unpacked', out)
        self.assertFalse(self.has('M997'))

    def test_a_refused_flash_leaves_dsf4_in_place(self) -> None:
        self.pi.reply = 'Error: Failed to find IAP file'
        status, _ = self.migrate('downgrade')
        self.assertEqual(status, 1)
        self.assertFalse(self.has('--force-confold'))

    def test_a_failed_reinstall_restarts_the_services_and_says_how_to_finish(self) -> None:
        self.failing = '--force-confold'
        status, out = self.migrate('downgrade')
        self.assertEqual(status, 1)
        self.assertIn('--dsf-only', out)
        self.assertTrue(self.has('systemctl restart'))

    def test_needs_the_dsf3_packages_underneath(self) -> None:
        self.packages = ''
        status, out = self.migrate('downgrade')
        self.assertEqual(status, 1)
        self.assertIn('no DSF 3 to go back to', out)
        self.assertEqual(self.pi.actions, [])

    def test_refuses_dsf4_packages(self) -> None:
        self.packages = self.packages.replace('duetcontrolserver 3.7.0', 'duetcontrolserver 4.0.0')
        status, out = self.migrate('downgrade')
        self.assertEqual(status, 1)
        self.assertIn('not DSF 3', out)

    def test_dsf_only_leaves_the_main_board_alone(self) -> None:
        self.pi.firmware = 'Duet3Firmware_MB6HC.bin'
        status, out = self.migrate('downgrade', '--dsf-only')
        self.assertEqual(status, 0, out)
        self.assertFalse(self.has('M997'))
        self.assertFalse(self.has('dpkg-deb'))


class EitherWay(MigrateTest):
    def test_does_nothing_on_a_machine_already_there(self) -> None:
        status, out = self.migrate('downgrade')
        self.assertEqual(status, 0, out)
        self.assertIn('already', out)
        self.assertEqual(self.pi.actions, [])

    def test_refuses_a_machine_part_way_through(self) -> None:
        self.pi.firmware = 'DuetCANMaster_MB6HC.bin'
        status, out = self.migrate('upgrade')
        self.assertEqual(status, 1)
        self.assertIn('--dsf-only', out)
        self.assertEqual(self.pi.actions, [])

    def test_refuses_a_busy_machine(self) -> None:
        self.pi.status = 'processing'
        status, out = self.migrate('upgrade')
        self.assertEqual(status, 1)
        self.assertIn('is processing', out)
        self.assertEqual(self.pi.actions, [])

    def test_refuses_a_standalone_board(self) -> None:
        self.open_session.side_effect = DuetError('http://pi does not serve the DSF REST API')
        status, out = self.migrate('upgrade')
        self.assertEqual(status, 1)
        self.assertIn('does not serve the DSF REST API', out)

    def test_needs_the_pi_reachable_over_ssh_to_start(self) -> None:
        with patch.object(subprocess, 'run', return_value=subprocess.CompletedProcess([], 255, stdout='')):
            status, _ = self.migrate('upgrade')
        self.assertEqual(status, 1)
        self.assertEqual(self.pi.actions, [])

    def test_a_dry_run_upgrade_changes_nothing(self) -> None:
        status, out = self.migrate('upgrade', '--dry-run')
        self.assertEqual(status, 0, out)
        self.assertIn('Would flash the main board (M997 P"DuetCANMaster_MB6HC.bin")', out)
        self.assertEqual(self.pi.actions, ['deploy DSF'])  # DeployOptions.dry_run makes this show what it would do
        self.assertEqual(self.pi.version, '3.7.0')

    def test_a_dry_run_downgrade_changes_nothing(self) -> None:
        self.on_dsf4()
        status, out = self.migrate('downgrade', '--dry-run')
        self.assertEqual(status, 0, out)
        self.assertIn('Would flash the main board (M997 P"Duet3Firmware_MB6HC.bin")', out)
        self.assertEqual(self.pi.actions, [])

    def test_a_dry_run_says_what_is_not_built(self) -> None:
        (self.build / 'DuetCANMaster_MB6HC/DuetCANMaster_MB6HC.bin').unlink()
        status, out = self.migrate('upgrade', '--dry-run')
        self.assertEqual(status, 1)
        self.assertIn('has not been built', out)
