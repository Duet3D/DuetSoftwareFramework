using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DuetEmulation;
using DuetEmulation.Boards;
using NUnit.Framework;

namespace EmulationTests.Harness;

/// <summary>
/// What the bench reads from each board's firmware headers, without an emulator: a header that changes
/// shape must fail here rather than leave the bench addressing the wrong pins
/// </summary>
[TestFixture]
public class BoardTypeTests
{
    private static IEnumerable<TestCaseData> Boards() => BoardTypes.All.Select(b => new TestCaseData(b).SetArgDisplayNames(b.FirmwarePreset));

    [TestCaseSource(nameof(Boards))]
    public void ThePinTableHasARowPerPinInOrder(BoardType board)
    {
        IReadOnlyList<PinTableEntry> entries = board.PinTable.Entries;

        Assert.That(entries, Is.Not.Empty);
        Assert.That(entries.Select(e => e.Pin.Index), Is.EqualTo(Enumerable.Range(0, entries.Count)));
    }

    [TestCaseSource(nameof(Boards))]
    public void EveryDriverHasAStepAndADirectionPin(BoardType board)
    {
        Assert.That(board.StepPins, Is.Not.Empty);
        Assert.That(board.DirectionPins, Has.Count.EqualTo(board.StepPins.Count));
        Assert.That(board.StepPins, Is.Unique);
    }

    [TestCaseSource(nameof(Boards))]
    public void TheThermistorSeriesResistorIsTheFirmwares(BoardType board) =>
        Assert.That(board.ThermistorSeriesResistance, Is.EqualTo(2200));

    [Test]
    public void TheTestScriptBuildsEveryImageTheBenchRuns()
    {
        // scripts/test.py emulation --build builds the presets its PRESETS list names
        string script = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "scripts", "common", "emulation_tests.py"));
        Match list = Regex.Match(script, @"^PRESETS = \[(?<presets>[^\]]*)\]", RegexOptions.Multiline);
        IEnumerable<string> listed = Regex.Matches(list.Groups["presets"].Value, "'([^']+)'").Select(m => m.Groups[1].Value);
        IEnumerable<string> needed = BoardTypes.All.SelectMany(b => new[] { b.FirmwarePreset, b.BootloaderPreset }).OfType<string>().Distinct();

        Assert.That(list.Success, "emulation_tests.py has a PRESETS list");
        Assert.That(listed, Is.EquivalentTo(needed));
    }

    [Test]
    public void TheExp3hcPortsAreWhereItsFirmwarePutsThem()
    {
        PinTable table = BoardTypes.Exp3hc.PinTable;

        Assert.Multiple(() =>
        {
            Assert.That(table["io0.in"].Pin, Is.EqualTo(McuPin.Parse("PA06")));
            Assert.That(table["io0.in"].ExternalInterrupt, Is.EqualTo(6));
            Assert.That(table["io1.in"].ExternalInterrupt, Is.EqualTo(2));
            Assert.That(table["out0"].PwmOutput, Is.EqualTo("tcc1_2F"));
            Assert.That(table["out1"].TimerOutput, Is.EqualTo("tc3_1"));
            Assert.That(table["temp0"].AdcInput, Is.EqualTo("adc1_5"));
            // Aliases name the same pin
            Assert.That(table["uart0.tx"].Pin, Is.EqualTo(table["io0.out"].Pin));
            Assert.That(BoardTypes.Exp3hc.StepPins[0], Is.EqualTo(McuPin.Parse("PA25")));
        });
    }

    [Test]
    public void TheMb6hcPortsAreWhereItsFirmwarePutsThem()
    {
        PinTable table = BoardTypes.Mb6hcStandalone.PinTable;

        Assert.Multiple(() =>
        {
            Assert.That(table["out0"].Pin, Is.EqualTo(McuPin.Parse("PA07")));
            Assert.That(table["out0"].PwmOutput, Is.EqualTo("pwm0h3_b"));
            Assert.That(table["out0"].ExternalInterrupt, Is.Null, "the SAME70 table has no EXTINT column");
            Assert.That(BoardTypes.Mb6hcStandalone.StepPins[0], Is.EqualTo(McuPin.Parse("PC18")));
        });
    }
}
