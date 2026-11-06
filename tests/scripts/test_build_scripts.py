# pyright: strict
"""The scripts that choose targets and act on their build output: list, build, clean and size."""

from __future__ import annotations

from unittest.mock import MagicMock, patch

import build
import clean
import list as list_script  # scripts/list.py, named so as not to hide the builtin
import size
from common import firmware
from common.dsf import PROJECTS
from common.firmware import load_boards
from common.targets import select

from .support import ScriptTest, run_main


class TargetSelectionTest(ScriptTest):
    """The target names, groups, -debug suffix, --all and --exclude that every target script takes."""

    def selected(self, *argv: str) -> tuple[list[str], list[str], list[str]]:
        """The DSF projects, the board presets and the boards named with -debug."""
        options = build.parse_options(argv)
        chosen = select(options.targets, options.all, options.exclude)
        return chosen.dsf, [b.preset for b in chosen.boards], sorted(chosen.debug_boards)

    def test_names_groups_all_and_exclude(self) -> None:
        all_boards = load_boards()
        d3e = [b.preset for b in all_boards if b.project == 'Duet3Expansion']
        self.assertEqual(self.selected('Duet3Firmware_EXP3HC', 'Duet3Firmware_TOOL1LC', 'Duet3Firmware_EXP3HC'), ([], ['Duet3Firmware_EXP3HC', 'Duet3Firmware_TOOL1LC'], []))
        # Boards come in CMakePresets.json order, and names are not case sensitive
        self.assertEqual(self.selected('duet3firmware_tool1lc', 'duet3firmware_exp3hc')[1], ['Duet3Firmware_EXP3HC', 'Duet3Firmware_TOOL1LC'])
        self.assertEqual(self.selected('d3e')[1], d3e)
        self.assertEqual(self.selected('Duet3Expansion', 'Duet3Firmware_EXP3HC')[1], d3e)
        self.assertEqual(self.selected('dsf'), (list(PROJECTS), [], []))
        self.assertEqual(self.selected('--all'), (list(PROJECTS), [b.preset for b in all_boards], []))
        self.assertEqual(self.selected('firmware', '-x', 'iap', '--exclude', 'Duet3Firmware_EXP1XD')[1],
                         [b.preset for b in all_boards if b.project != 'DuetIAP' and b.preset != 'Duet3Firmware_EXP1XD'])
        self.assertEqual(self.selected('rrf', 'bootloader', '-x', 'Duet3Firmware_MB6HC_CAN0')[1],
                         [b.preset for b in all_boards if b.project in ('RepRapFirmware', 'Duet3Bootloader')
                          and b.preset != 'Duet3Firmware_MB6HC_CAN0'])
        self.assertEqual(self.selected('DuetControlServer', 'canmaster')[0:2],
                         (['DuetControlServer'], [b.preset for b in all_boards if b.project == 'DuetCANMaster']))

    def test_debug_suffix(self) -> None:
        self.assertEqual(self.selected('Duet3Firmware_EXP3HC-debug', 'Duet3Firmware_TOOL1LC'), ([], ['Duet3Firmware_EXP3HC', 'Duet3Firmware_TOOL1LC'], ['Duet3Firmware_EXP3HC']))
        self.assertEqual(self.selected('d3e-debug')[2], sorted(self.selected('d3e')[1]))

    def test_bad_selections(self) -> None:
        for argv, error in ((['NOPE'], 'Unknown target "NOPE"'), (['-x', 'nope', 'Duet3Firmware_EXP3HC'], 'Unknown target "nope"'),
                            ([], 'Name some targets'), (['Duet3Firmware_EXP3HC', '-x', 'd3e'], 'Name some targets'),
                            (['DuetControlServer-debug'], 'only firmware boards have Debug builds')):
            with self.subTest(argv=argv), self.assertRaisesRegex(SystemExit, error):
                self.selected(*argv)

    def test_kinds(self) -> None:
        with self.assertRaisesRegex(SystemExit, 'This takes firmware boards, not DSF projects'):
            select(['DuetControlServer'], kinds=('firmware',))
        with self.assertRaisesRegex(SystemExit, 'This takes DSF projects, not firmware boards'):
            select(['Duet3Firmware_EXP3HC'], kinds=('dsf',))


class ListTest(ScriptTest):
    def test_lists_a_project_marking_the_built_boards(self) -> None:
        self.built('Duet3Firmware_EXP3HC', 'Duet3Firmware_EXP3HC.elf')
        self.built('Duet3Firmware_TOOL1LC', 'CMakeCache.txt')  # configured is not built
        status, out = run_main(list_script.main, 'd3e')
        self.assertEqual(status, 0)
        self.assertIn('Duet3Expansion', out)
        self.assertNotIn('RepRapFirmware', out)
        self.assertNotIn('DSF', out)
        self.assertRegex(out, r'Duet3Firmware_EXP3HC .*\(built\)')
        self.assertNotRegex(out, r'Duet3Firmware_TOOL1LC .*\(built\)')

    def test_everything_lists_the_groups(self) -> None:
        status, out = run_main(list_script.main)
        self.assertEqual(status, 0)
        self.assertIn('Groups: dsf', out)
        for name in ('DuetControlServer', 'TracyClient', 'DuetCANMaster_MB6HC', 'Duet3Firmware_EXP3HC'):
            self.assertIn(name, out)

    def test_names(self) -> None:
        self.assertEqual(run_main(list_script.main, '-n', 'Duet3Firmware_EXP3HC', 'Duet3Firmware_TOOL1LC'), (0, 'Duet3Firmware_EXP3HC\nDuet3Firmware_TOOL1LC\n'))
        self.assertEqual(run_main(list_script.main, '--names', '--debug', 'Duet3Firmware_EXP3HC'), (0, 'Duet3Firmware_EXP3HC-debug\n'))
        self.assertEqual(run_main(list_script.main, '-n', 'Duet3Firmware_EXP3HC-debug', 'DuetWebServer'), (0, 'DuetWebServer\nDuet3Firmware_EXP3HC-debug\n'))


class BuildTest(ScriptTest):
    def setUp(self) -> None:
        super().setUp()
        self.commands: list[list[str]] = []
        self.failing: set[str] = set()

        def run(command: list[str], env: dict[str, str] | None = None) -> bool:
            self.commands.append(command)
            return command[-1] not in self.failing

        self.enterContext(patch.object(firmware, 'run', run))
        self.build_dsf: MagicMock = self.enterContext(patch.object(build, 'build_dsf', return_value=True))
        self.build_tracy: MagicMock = self.enterContext(patch.object(build, 'build_tracy'))

    def test_options_reach_cmake(self) -> None:
        self.assertEqual(run_main(build.main, 'Duet3Firmware_EXP3HC')[0], 0)
        self.assertEqual(self.commands, [['cmake', '--preset', 'Duet3Firmware_EXP3HC'], ['cmake', '--build', '--preset', 'Duet3Firmware_EXP3HC']])
        self.commands.clear()
        self.assertEqual(run_main(build.main, 'Duet3Firmware_EXP3HC', '-d', '-j', '4', '-v', '-f', '-c')[0], 0)
        self.assertEqual(self.commands, [
            ['cmake', '--preset', 'Duet3Firmware_EXP3HC-debug', '--fresh'],
            ['cmake', '--build', '--preset', 'Duet3Firmware_EXP3HC-debug', '--clean-first', '--parallel', '4', '--verbose'],
        ])
        self.build_dsf.assert_not_called()

    def test_long_options_and_debug_suffix(self) -> None:
        run_main(build.main, 'Duet3Firmware_EXP3HC', '--debug', '--jobs', '2', '--verbose', '--fresh', '--clean')
        self.assertEqual(self.commands[-1], ['cmake', '--build', '--preset', 'Duet3Firmware_EXP3HC-debug', '--clean-first',
                                             '--parallel', '2', '--verbose'])
        self.commands.clear()
        run_main(build.main, 'Duet3Firmware_EXP3HC-debug', 'Duet3Firmware_TOOL1LC')
        self.assertEqual([c[-1] for c in self.commands], ['Duet3Firmware_EXP3HC-debug', 'Duet3Firmware_EXP3HC-debug', 'Duet3Firmware_TOOL1LC', 'Duet3Firmware_TOOL1LC'])

    def test_stops_at_a_failure_unless_keep_going(self) -> None:
        self.failing = {'Duet3Firmware_EXP3HC'}
        status, out = run_main(build.main, 'Duet3Firmware_EXP3HC', 'Duet3Firmware_TOOL1LC')
        self.assertEqual(status, 1)
        self.assertNotIn(['cmake', '--preset', 'Duet3Firmware_TOOL1LC'], self.commands)
        self.assertRegex(out, r'Duet3Firmware_TOOL1LC\s+skipped')
        self.assertIn('Failed: Duet3Firmware_EXP3HC', out)
        self.commands.clear()
        status, _ = run_main(build.main, 'Duet3Firmware_EXP3HC', 'Duet3Firmware_TOOL1LC', '--keep-going')
        self.assertEqual(status, 1)
        self.assertIn(['cmake', '--preset', 'Duet3Firmware_TOOL1LC'], self.commands)
        self.assertEqual(run_main(build.main, 'Duet3Firmware_EXP3HC', 'Duet3Firmware_TOOL1LC', '-k')[0], 1)

    def test_markdown_summary(self) -> None:
        summary = self.root / 'summary.md'
        run_main(build.main, 'Duet3Firmware_EXP3HC', '--markdown', str(summary))
        self.assertIn('| Duet3Firmware_EXP3HC | ok |', summary.read_text())
        self.assertIn('| Duet3Firmware_EXP3HC | ok |', run_main(build.main, 'Duet3Firmware_EXP3HC', '--markdown', '-')[1])

    def test_dsf_projects_then_firmware(self) -> None:
        self.assertEqual(run_main(build.main, 'DuetControlServer', 'TracyClient', 'Duet3Firmware_EXP3HC', '--debug')[0], 0)
        options, projects = self.build_dsf.call_args.args
        self.assertEqual((projects, options.configuration), (['DuetControlServer'], 'Debug'))
        self.build_tracy.assert_called_once()
        self.assertEqual(self.commands[-1][-1], 'Duet3Firmware_EXP3HC-debug')

    def test_a_dsf_failure_stops_the_firmware_unless_keep_going(self) -> None:
        self.build_dsf.return_value = False
        self.assertEqual(run_main(build.main, 'dsf', 'Duet3Firmware_EXP3HC')[0], 1)
        self.assertEqual(self.commands, [])
        self.assertEqual(run_main(build.main, 'dsf', 'Duet3Firmware_EXP3HC', '-k')[0], 1)
        self.assertEqual(self.commands[-1][-1], 'Duet3Firmware_EXP3HC')


class CleanTest(ScriptTest):
    def setUp(self) -> None:
        super().setUp()
        self.runs: MagicMock = self.enterContext(patch.object(clean, 'run', return_value=True))
        self.enterContext(patch.object(clean, 'ROOT', self.root))
        self.enterContext(patch.object(clean, 'PLUGIN_BUILD_DIR', self.build / 'plugins'))
        self.built('Duet3Firmware_EXP3HC')
        self.built('Duet3Firmware_EXP3HC-debug')
        for tree in ('Duet3Firmware_EXP3HC', 'Duet3Firmware_EXP3HC-debug'):
            (self.build / tree / 'CMakeCache.txt').touch()

    def test_cleans_the_built_trees_of_both_variants(self) -> None:
        self.assertEqual(run_main(clean.main, 'Duet3Firmware_EXP3HC', 'Duet3Firmware_TOOL1LC')[0], 0)
        self.assertEqual([c.args[0] for c in self.runs.call_args_list], [
            ['cmake', '--build', str(self.build / 'Duet3Firmware_EXP3HC'), '--target', 'clean'],
            ['cmake', '--build', str(self.build / 'Duet3Firmware_EXP3HC-debug'), '--target', 'clean'],
        ])

    def test_purge_deletes_the_trees(self) -> None:
        self.assertEqual(run_main(clean.main, 'd3e', '--purge')[0], 0)
        self.runs.assert_not_called()
        self.assertEqual(list(self.build.iterdir()), [])

    def test_dsf_projects(self) -> None:
        self.assertEqual(run_main(clean.main, 'DuetControlServer')[0], 0)
        self.assertEqual([c.args[0][:2] + c.args[0][3:5] for c in self.runs.call_args_list],
                         [['dotnet', 'clean', '-c', 'Debug'], ['dotnet', 'clean', '-c', 'Release']])
        output = self.root / PROJECTS['DuetControlServer'] / 'bin'
        output.mkdir(parents=True)
        self.runs.reset_mock()
        self.assertEqual(run_main(clean.main, 'DuetControlServer', '--purge')[0], 0)
        self.runs.assert_not_called()
        self.assertFalse(output.exists())


class SizeTest(ScriptTest):
    def test_every_built_board_by_default(self) -> None:
        self.built('Duet3Firmware_EXP3HC', 'Duet3Firmware_EXP3HC.elf')
        status, out = run_main(size.main)
        self.assertEqual(status, 0)
        self.assertRegex(out, r'Duet3Firmware_EXP3HC\s+ok')
        self.assertNotIn('Duet3Firmware_TOOL1LC', out)

    def test_named_boards_debug_and_markdown(self) -> None:
        self.built('Duet3Firmware_EXP3HC-debug', 'Duet3Firmware_EXP3HC.elf')
        status, out = run_main(size.main, 'Duet3Firmware_EXP3HC', 'Duet3Firmware_TOOL1LC', '-d', '--markdown', '-')
        self.assertEqual(status, 0)
        self.assertIn('| Duet3Firmware_EXP3HC-debug | ok |', out)
        self.assertIn('| Duet3Firmware_TOOL1LC-debug | not built |', out)

    def test_nothing_built(self) -> None:
        self.assertEqual(run_main(size.main), (1, 'No board has been built\n'))

    def test_takes_only_boards(self) -> None:
        self.assertIn('This takes firmware boards, not DSF projects', run_main(size.main, 'DuetControlServer')[1])
