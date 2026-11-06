# pyright: strict
"""deploy.py's firmware deployment, against a fake main board with an EXP3HC at CAN address 1 and a
TOOL1LC at 20, standalone or with an SBC."""

from __future__ import annotations

import re
import time
from collections.abc import Callable
from unittest.mock import MagicMock, patch

import deploy
from common import firmware_deploy
from common.duet import Connector

from .support import ScriptTest, run_main


class FakeDuet(Connector):
    """A main board's object model, in which an expansion board flashes and restarts when M997 updates it."""
    mode = 'standalone'
    sbc = False

    def __init__(self) -> None:
        super().__init__('http://duet3.local', None, 1, 1)
        self.boards: list[dict[str, object]] = [
            {'canAddress': 0, 'name': 'Duet 3 MB6HC', 'shortName': 'MB6HC', 'state': 'running',
             'firmwareFileName': 'Duet3Firmware_MB6HC.bin', 'iapFileNameSD': 'Duet3_SDiap32_MB6HC.bin',
             'iapFileNameSBC': 'Duet3_SBCiap32_MB6HC.bin'},
            {'canAddress': 1, 'name': 'Duet 3 Expansion 3HC', 'shortName': 'EXP3HC', 'state': 'running',
             'firmwareFileName': 'Duet3Firmware_EXP3HC.bin'},
            {'canAddress': 20, 'name': 'Duet 3 Toolboard 1LC', 'shortName': 'TOOL1LC', 'state': 'running',
             'firmwareFileName': 'Duet3Firmware_TOOL1LC.bin'},
        ]
        self.sent: list[str] = []
        self.uploads: dict[str, bytes] = {}
        self.pending: dict[int, list[str]] = {}  # the states boards being updated go through

    def connect(self) -> None: pass
    def code(self, gcode: str, wait: bool = True) -> str: return self.send(gcode)
    def replies(self) -> str: return ''
    def filelist(self, directory: str) -> list[dict[str, object]]: return []
    def fileinfo(self, name: str) -> object: return None
    def download(self, name: str) -> bytes: return b''
    def delete(self, name: str, recursive: bool) -> None: pass
    def move(self, old: str, new: str, force: bool) -> None: pass
    def mkdir(self, directory: str) -> None: pass

    def send(self, gcode: str) -> str:
        self.sent.append(gcode)
        if found := re.fullmatch(r'M997 B(\d+)', gcode):
            self.pending[int(found[1])] = ['flashing', 'resetting', 'running']
        return ''

    def model(self, key: str, flags: str | None = None) -> object:
        assert key == 'boards'
        for b in self.boards:
            if states := self.pending.get(int(str(b['canAddress']))):
                b['state'] = states.pop(0)
        return [dict(b) for b in self.boards]

    def upload(self, name: str, data: bytes, modified: str | None = None) -> None:
        self.uploads[name] = data


class DeployTest(ScriptTest):
    def setUp(self) -> None:
        super().setUp()
        self.duet = FakeDuet()
        self.open_session = self.enterContext(patch.object(firmware_deploy, 'open_session', return_value=self.duet))
        self.build_preset = self.enterContext(patch.object(firmware_deploy, 'build', return_value=True))
        self.enterContext(patch.object(firmware_deploy, 'bootloader_names',
                                       return_value={'EXP3HC': 'SAME5x', 'TOOL1LC': 'SAMC21'}))
        self.enterContext(patch.object(time, 'sleep'))
        for preset in ('Duet3Firmware_MB6HC', 'Duet3Firmware_MB6HC-debug'):
            self.built(preset, 'Duet3Firmware_MB6HC.bin', data=preset.encode())
        self.built('Duet3Firmware_MB6HC_no_SD', 'Duet3Firmware_MB6HC_no_SD.bin')
        self.built('Duet3Firmware_EXP3HC', 'Duet3Firmware_EXP3HC.bin', data=b'Duet3Firmware_EXP3HC')
        self.built('Duet3Firmware_EXP3HC-debug', 'Duet3Firmware_EXP3HC.bin', data=b'Duet3Firmware_EXP3HC-debug')
        self.built('Duet3_SDiap32_MB6HC', 'Duet3_SDiap32_MB6HC.bin')
        self.built('Duet3Bootloader-SAME5x', 'Duet3Bootloader-SAME5x.bin')
        self.built('DuetCANMaster_MB6HC', 'DuetCANMaster_MB6HC.bin', data=b'canmaster')
        self.built('Duet3_SBCiap32_MB6HC', 'Duet3_SBCiap32_MB6HC.bin')

    def deploy(self, *argv: str, targets: tuple[str, ...] = ('firmware',)) -> tuple[int, str]:
        """deploy.py through the fake board, of every board unless other targets are given."""
        return run_main(deploy.main, '-H', 'duet3.local', *targets, *argv)

    def updated(self, out: str) -> list[str]:
        return re.findall(r'Would update (.*)', out)

    def test_dry_run_plans_every_built_board_main_last(self) -> None:
        status, out = self.deploy('-n')
        self.assertEqual(status, 0)
        self.assertEqual(self.open_session.call_args.args[:2], ('duet3.local', 'reprap'))
        self.assertIn('Would upload Duet3Firmware_MB6HC/Duet3Firmware_MB6HC.bin to 0:/firmware/Duet3Firmware_MB6HC.bin', out)
        self.assertIn('Would upload Duet3_SDiap32_MB6HC/Duet3_SDiap32_MB6HC.bin', out)
        self.assertIn('Would upload Duet3Firmware_EXP3HC/Duet3Firmware_EXP3HC.bin', out)
        self.assertIn('Skipping Duet 3 Toolboard 1LC (TOOL1LC) at CAN address 20: Duet3Firmware_TOOL1LC.bin has not been built', out)
        self.assertEqual(self.updated(out), ['Duet 3 Expansion 3HC (EXP3HC) at CAN address 1 (M997 B1)',
                                             'Duet 3 MB6HC (MB6HC) (M997)'])
        self.assertEqual((self.duet.sent, self.duet.uploads), ([], {}))
        self.assertEqual(self.deploy('--dry-run')[1], out)

    def test_password_and_debug(self) -> None:
        status, out = self.deploy('-P', 'secret', '-d', '-n')
        self.assertEqual(status, 0)
        self.assertEqual(self.open_session.call_args.args[:2], ('duet3.local', 'secret'))
        self.assertIn('Would upload Duet3Firmware_MB6HC-debug/Duet3Firmware_MB6HC.bin', out)
        self.assertIn('Would upload Duet3Firmware_EXP3HC-debug/Duet3Firmware_EXP3HC.bin', out)
        self.assertIn('Would upload Duet3_SDiap32_MB6HC/', out)  # the IAP is always Release
        self.deploy('--password', 'other', '--debug', '-n')
        self.assertEqual(self.open_session.call_args.args[1], 'other')

    def test_choosing_boards_by_address_and_type(self) -> None:
        main = 'Duet 3 MB6HC (MB6HC) (M997)'
        exp = 'Duet 3 Expansion 3HC (EXP3HC) at CAN address 1 (M997 B1)'
        for argv, targets, expected in ((['-b', '0'], ('firmware',), [main]), (['--board', '1'], ('firmware',), [exp]),
                                        ([], ('Duet3Firmware_EXP3HC',), [exp]), ([], ('Duet3Firmware_MB6HC',), [main]),
                                        (['-b', '1'], ('Duet3Firmware_MB6HC',), [exp, main])):
            with self.subTest(argv=argv, targets=targets):
                self.assertEqual(self.updated(self.deploy('-n', *argv, targets=targets)[1]), expected)
        status, out = self.deploy('-n', targets=('Duet3Firmware_EXP1XD',))
        self.assertEqual(status, 1)
        self.assertIn('No connected board matches the boards asked for\n', out)

    def test_main_preset(self) -> None:
        self.assertIn('Would upload Duet3Firmware_MB6HC_no_SD/Duet3Firmware_MB6HC_no_SD.bin to 0:/firmware/Duet3Firmware_MB6HC.bin',
                      self.deploy('-n', '-m', 'Duet3Firmware_MB6HC_no_SD')[1])
        self.built('Duet3Firmware_MB6HC_no_SD-debug', 'Duet3Firmware_MB6HC_no_SD.bin')
        self.assertIn('Would upload Duet3Firmware_MB6HC_no_SD-debug/Duet3Firmware_MB6HC_no_SD.bin',
                      self.deploy('-n', '--main', 'Duet3Firmware_MB6HC_no_SD', '-d')[1])
        self.assertIn('Unknown target "NOPE"', self.deploy('-m', 'NOPE')[1])

    def test_main_preset_with_an_image_of_another_name(self) -> None:
        self.built('Duet3Firmware_MB6HC_CAN0', 'Duet3Firmware_MB6HC_CAN0.bin')
        self.assertIn('Would upload Duet3Firmware_MB6HC_CAN0/Duet3Firmware_MB6HC_CAN0.bin to 0:/firmware/Duet3Firmware_MB6HC.bin',
                      self.deploy('-n', '-m', 'Duet3Firmware_MB6HC_CAN0')[1])

    def test_finds_a_variant_by_the_file_it_builds(self) -> None:
        for preset in ('Duet3Firmware_MB6HC_CAN0', 'Duet3Firmware_MB6HC_no_SD'):
            with self.subTest(preset=preset):
                image = preset + '.bin'
                self.duet.boards[0]['firmwareFileName'] = image
                self.built(preset, image)
                self.assertIn(f'Would upload {preset}/{image} to 0:/firmware/{image}', self.deploy('-n')[1])

    def test_a_file_left_by_an_earlier_build_is_not_used(self) -> None:
        self.built('Duet3Firmware_TOOL1LC', 'Duet3Firmware_TOOL1LC_old.bin')
        self.assertIn('Duet3Firmware_TOOL1LC.bin has not been built', self.deploy('-n')[1])

    def test_upload_only(self) -> None:
        for flag in ('--upload-only',):
            self.duet.uploads.clear()
            self.assertEqual(self.deploy(flag)[0], 0)
            self.assertEqual(self.duet.uploads, {'0:/firmware/Duet3Firmware_MB6HC.bin': b'Duet3Firmware_MB6HC',
                                                 '0:/firmware/Duet3_SDiap32_MB6HC.bin': b'image',
                                                 '0:/firmware/Duet3Firmware_EXP3HC.bin': b'Duet3Firmware_EXP3HC'})
            self.assertEqual(self.duet.sent, [])

    def test_updates_an_expansion_board(self) -> None:
        status, out = self.deploy('-b', '1')
        self.assertEqual(status, 0)
        self.assertEqual(list(self.duet.uploads), ['0:/firmware/Duet3Firmware_EXP3HC.bin'])
        self.assertEqual(self.duet.sent, ['M997 B1'])
        self.assertIn('Updating Duet 3 Expansion 3HC (EXP3HC) at CAN address 1\n  flashing\n  resetting\n  running', out)

    def test_bootloader(self) -> None:
        status, out = self.deploy('--bootloader', '-n')
        self.assertEqual(status, 0)
        self.assertIn('Would upload Duet3Bootloader-SAME5x/Duet3Bootloader-SAME5x.bin to 0:/firmware/Duet3Bootloader-SAME5x.bin', out)
        self.assertIn('TOOL1LC) at CAN address 20: Duet3Bootloader-SAMC21.bin has not been built', out)
        self.assertEqual(self.updated(out), ['the bootloader of Duet 3 Expansion 3HC (EXP3HC) at CAN address 1 (M997 B1 S3)'])

    def test_build_and_clean(self) -> None:
        expected = ['Duet3Firmware_MB6HC', 'Duet3_SDiap32_MB6HC', 'Duet3Firmware_EXP3HC', 'Duet3Firmware_TOOL1LC']
        out = self.deploy('--build', '-n')[1]
        self.assertEqual(re.findall(r'Would build (\S+)\n', out), expected)
        self.build_preset.assert_not_called()
        self.assertEqual(re.findall(r'Would build (\S+) from clean', self.deploy('-c', '-n')[1]), expected)
        self.assertEqual(re.findall(r'Would build (\S+)', self.deploy('--build', '--bootloader', '-n')[1]),
                         ['Duet3Bootloader-SAME5x', 'Duet3Bootloader-SAMC21'])

        for flag, clean in (('--build', False), ('--clean', True)):
            with self.subTest(flag=flag):
                self.build_preset.reset_mock()
                self.assertEqual(self.deploy(flag, '-d', '--upload-only')[0], 0)
                self.assertEqual([(c.args[0], c.kwargs['clean']) for c in self.build_preset.call_args_list],
                                 [(p, clean) for p in ('Duet3Firmware_MB6HC-debug', 'Duet3_SDiap32_MB6HC', 'Duet3Firmware_EXP3HC-debug', 'Duet3Firmware_TOOL1LC-debug')])

    def test_build_failure(self) -> None:
        self.build_preset.return_value = False
        status, out = self.deploy('--build')
        self.assertEqual(status, 1)
        self.assertIn('Building Duet3Firmware_MB6HC failed', out)
        self.assertEqual(self.duet.uploads, {})

    def test_sbc(self) -> None:
        """With an SBC the main board runs DuetCANMaster and writes its firmware with the SBC IAP."""
        self.duet.sbc = True
        self.duet.boards[0]['firmwareFileName'] = 'DuetCANMaster_MB6HC.bin'
        status, out = self.deploy('-n')
        self.assertEqual(status, 0)
        self.assertIn('Would upload DuetCANMaster_MB6HC/DuetCANMaster_MB6HC.bin to 0:/firmware/DuetCANMaster_MB6HC.bin', out)
        self.assertIn('Would upload Duet3_SBCiap32_MB6HC/Duet3_SBCiap32_MB6HC.bin', out)
        self.assertNotIn('Duet3Firmware_MB6HC/', out.replace('DuetCANMaster_MB6HC/', ''))
        self.assertEqual(re.findall(r'Would build (\S+)\n', self.deploy('--build', '-n')[1]),
                         ['DuetCANMaster_MB6HC', 'Duet3_SBCiap32_MB6HC', 'Duet3Firmware_EXP3HC', 'Duet3Firmware_TOOL1LC'])
        self.assertIn('Would upload DuetCANMaster_MB6HC/', self.deploy('-n', targets=('DuetCANMaster_MB6HC',))[1])

    def test_sbc_with_a_main_board_in_expansion_mode(self) -> None:
        """A main board on the bus runs RepRapFirmware on an SBC as anywhere else. Given DuetCANMaster, which
        does not speak until an SBC talks to it over SPI, it would vanish from the bus."""
        self.duet.sbc = True
        self.duet.boards[0]['firmwareFileName'] = 'DuetCANMaster_MB6HC.bin'
        self.duet.boards.append({'canAddress': 2, 'name': 'Duet 3 MB6HC', 'shortName': 'MB6HC', 'state': 'running',
                                 'firmwareFileName': 'Duet3Firmware_MB6HC.bin'})
        status, out = self.deploy('-n')
        self.assertEqual(status, 0)
        self.assertIn('Would upload Duet3Firmware_MB6HC/Duet3Firmware_MB6HC.bin to 0:/firmware/Duet3Firmware_MB6HC.bin', out)
        self.assertIn('Would upload DuetCANMaster_MB6HC/DuetCANMaster_MB6HC.bin to 0:/firmware/DuetCANMaster_MB6HC.bin', out)
        self.assertEqual(self.deploy('--upload-only')[0], 0)
        self.assertEqual(self.duet.uploads['0:/firmware/Duet3Firmware_MB6HC.bin'], b'Duet3Firmware_MB6HC')
        self.assertEqual(self.duet.uploads['0:/firmware/DuetCANMaster_MB6HC.bin'], b'canmaster')

    def test_sbc_with_nothing_built_for_an_expansion_mode_main_board(self) -> None:
        """DuetCANMaster is no stand-in when RepRapFirmware has not been built for it."""
        self.duet.sbc = True
        self.duet.boards[0]['firmwareFileName'] = 'DuetCANMaster_MB6HC.bin'
        self.duet.boards.append({'canAddress': 2, 'name': 'Duet 3 MB6HC', 'shortName': 'MB6HC', 'state': 'running',
                                 'firmwareFileName': 'Duet3Firmware_MB6HC.bin'})
        (firmware_deploy.BUILD / 'Duet3Firmware_MB6HC' / 'Duet3Firmware_MB6HC.bin').unlink()
        _, out = self.deploy('-n')
        self.assertIn('Skipping Duet 3 MB6HC (MB6HC) at CAN address 2: Duet3Firmware_MB6HC.bin has not been built', out)
        self.assertNotIn('to 0:/firmware/Duet3Firmware_MB6HC.bin', out)

    def deploy_both(self, *argv: str, firmware: int = 0, built: bool = True) -> tuple[int, str, list[str]]:
        """deploy.py of DSF and the firmware, with what it did in order: its status, output and steps."""
        steps: list[str] = []

        def step(name: str, result: object) -> Callable[..., object]:
            def run(*_: object, **__: object) -> object:
                steps.append(name)
                return result
            return run

        with (patch.object(deploy, 'plan', return_value=MagicMock()),
              patch.object(deploy, 'binary_sources', return_value=[]),
              patch.object(deploy, 'build_projects', side_effect=step('build DSF', built)),
              patch.object(deploy, 'deploy', side_effect=step('deploy DSF', True)) as deploy_dsf,
              patch.object(firmware_deploy, 'deploy_firmware', side_effect=step('deploy firmware', firmware))):
            status, out = self.deploy(*argv, targets=('dsf', 'firmware'))
        self.deploy_options = deploy_dsf.call_args.args[1] if deploy_dsf.called else None
        return status, out, steps

    def test_firmware_goes_before_dsf(self) -> None:
        """The DSF that is running updates the firmware, and the new DSF is left running. It is built
        first, as a failure then leaves the firmware alone."""
        status, _, steps = self.deploy_both('--build')
        self.assertEqual(status, 0)
        self.assertEqual(steps, ['build DSF', 'deploy firmware', 'deploy DSF'])
        assert self.deploy_options is not None
        self.assertTrue(self.deploy_options.start_services)

    def test_dsf_build_failure_leaves_the_firmware_alone(self) -> None:
        status, _, steps = self.deploy_both('--build', built=False)
        self.assertEqual(status, 1)
        self.assertEqual(steps, ['build DSF'])

    def test_firmware_failure_leaves_dsf_alone(self) -> None:
        """DSF is what updates the firmware, so it has to stay in place to try again."""
        status, out, steps = self.deploy_both('--build', firmware=1)
        self.assertEqual(status, 1)
        self.assertEqual(steps, ['build DSF', 'deploy firmware'])
        self.assertIn('Not deploying DSF', out)

    def test_dsf_not_built_leaves_the_firmware_alone(self) -> None:
        with patch.object(deploy, 'binary_sources', side_effect=SystemExit('not built')):
            with patch.object(deploy, 'plan', return_value=MagicMock()):
                status, out = self.deploy(targets=('dsf', 'firmware'))
        self.assertNotEqual(status, 0)
        self.assertIn('not built', out)
        self.open_session.assert_not_called()

    def test_host_or_local(self) -> None:
        status, out = run_main(deploy.main, 'firmware')
        self.assertNotEqual(status, 0)
        self.assertIn('--host, or --local', out)
        self.open_session.assert_not_called()
