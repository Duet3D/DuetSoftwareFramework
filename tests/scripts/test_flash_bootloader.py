# pyright: strict
"""flash_bootloader.py, with GDB faked: it records the command file and reads back the image it was given."""

from __future__ import annotations

import re
from collections.abc import Sequence
from pathlib import Path
from unittest.mock import patch

import flash_bootloader
from common.firmware import Elf, Segment

from .support import ScriptTest, run_main

IMAGE = b'bootloader'


class FlashBootloaderTest(ScriptTest):
    def setUp(self) -> None:
        super().setUp()
        self.scripts: list[str] = []
        self.readback = IMAGE
        self.enterContext(patch.object(flash_bootloader, 'run', self.gdb))
        self.enterContext(patch.object(flash_bootloader, 'read_elf',
                                       return_value=Elf([], [], [Segment(0, 0, len(IMAGE), len(IMAGE))])))
        self.build_preset = self.enterContext(patch.object(flash_bootloader, 'build', return_value=True))
        for preset in ('Duet3Bootloader-SAME5x', 'Duet3Bootloader-SAME5x-debug'):
            self.built(preset, 'Duet3Bootloader-SAME5x.elf', 'Duet3Bootloader-SAME5x.bin', data=IMAGE)

    def gdb(self, command: Sequence[str], env: dict[str, str] | None = None) -> bool:
        script = Path(command[-1]).read_text()
        self.scripts.append(script)
        found = re.search(r'dump binary memory (\S+readback\.bin)', script)
        assert found
        Path(found[1]).write_bytes(self.readback)
        return True

    def test_flashes_through_the_default_server(self) -> None:
        status, out = run_main(flash_bootloader.main, 'Duet3Bootloader-SAME5x')
        self.assertEqual(status, 0)
        self.assertIn('Flashed Duet3Bootloader-SAME5x/Duet3Bootloader-SAME5x.bin to 0x00000000', out)
        script = self.scripts[0]
        self.assertIn('target extended-remote host.docker.internal:2331\n', script)
        self.assertIn('monitor atsame5 bootloader 0\n', script)
        self.assertIn(f'load {self.build}/Duet3Bootloader-SAME5x/Duet3Bootloader-SAME5x.bin 0x0\n', script)
        self.build_preset.assert_not_called()

    def test_server_and_debug(self) -> None:
        for argv, preset in ((['-s', 'localhost:3333'], 'Duet3Bootloader-SAME5x'),
                             (['--server', 'localhost:3333', '-d'], 'Duet3Bootloader-SAME5x-debug'),
                             (['--server=localhost:3333', '--debug'], 'Duet3Bootloader-SAME5x-debug')):
            with self.subTest(argv=argv):
                self.assertEqual(run_main(flash_bootloader.main, 'Duet3Bootloader-SAME5x', *argv)[0], 0)
                self.assertIn('target extended-remote localhost:3333\n', self.scripts[-1])
                self.assertIn(f'/{preset}/', self.scripts[-1])
        run_main(flash_bootloader.main, 'Duet3Bootloader-SAME5x-debug')  # a -debug preset is the Debug build
        self.assertIn('/Duet3Bootloader-SAME5x-debug/', self.scripts[-1])

    def test_build_and_clean(self) -> None:
        for argv, preset, clean in ((['--build'], 'Duet3Bootloader-SAME5x', False),
                                    (['-c', '-d'], 'Duet3Bootloader-SAME5x-debug', True),
                                    (['--clean'], 'Duet3Bootloader-SAME5x', True)):
            with self.subTest(argv=argv):
                self.build_preset.reset_mock()
                self.assertEqual(run_main(flash_bootloader.main, 'Duet3Bootloader-SAME5x', *argv)[0], 0)
                self.build_preset.assert_called_once_with(preset, clean=clean)
        self.build_preset.return_value = False
        self.assertIn('Building Duet3Bootloader-SAME5x failed', run_main(flash_bootloader.main, 'Duet3Bootloader-SAME5x', '--build')[1])

    def test_refusals(self) -> None:
        for argv, error in ((['Duet3Firmware_EXP3HC'], 'Duet3Firmware_EXP3HC is not a bootloader'),
                            (['Duet3_CANiap32_MB6HC'], 'is not a bootloader'),  # a CAN IAP
                            (['Duet3Bootloader-SAMC21'], 'Duet3Bootloader-SAMC21 has not been built'),
                            ([], 'the following arguments are required: preset')):
            with self.subTest(argv=argv):
                status, out = run_main(flash_bootloader.main, *argv)
                self.assertNotEqual(status, 0)
                self.assertIn(error, out)
        self.assertEqual(self.scripts, [])

    def test_readback_mismatch(self) -> None:
        self.readback = b'corrupted'
        self.assertEqual(run_main(flash_bootloader.main, 'Duet3Bootloader-SAME5x'), (1, 'The bootloader did not read back as written\n'))
