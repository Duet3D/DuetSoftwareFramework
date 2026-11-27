using System.Collections.Generic;
using System.Linq;
using DuetEmulation.Boards;
using NUnit.Framework;

namespace EmulationTests.Harness;

/// <summary>
/// What each port of a board is for, from the names in its firmware's pin table, without an emulator
/// </summary>
[TestFixture]
public class BoardPortTests
{
    [TestCase("io0.in", false, PortKind.DigitalInput)]
    [TestCase("io12.in", false, PortKind.DigitalInput)]
    [TestCase("out3.tach", false, PortKind.TachoInput)]
    [TestCase("out0", false, PortKind.Output)]
    [TestCase("io0.out", false, PortKind.Output)]
    [TestCase("temp0", true, PortKind.AnalogInput)]
    [TestCase("temp0", false, PortKind.Other)]
    [TestCase("ate.vin", true, PortKind.TestPoint)]
    [TestCase("spi.cs0", false, PortKind.Other)]
    [TestCase("pdec.a", false, PortKind.Other)]
    public void APortsKindFollowsItsName(string name, bool analog, PortKind kind) =>
        Assert.That(BoardPorts.Kind(name, analog), Is.EqualTo(kind));

    [Test]
    public void AnExp3hcHasItsInputsOutputsAndThermistors()
    {
        IReadOnlyList<BoardPort> ports = BoardPorts.Of(BoardTypes.Exp3hc);
        string[] Names(PortKind kind) => [.. ports.Where(p => p.Kind == kind).Select(p => p.Name)];

        Assert.Multiple(() =>
        {
            Assert.That(Names(PortKind.DigitalInput), Is.EqualTo(new[] { "io0.in", "io1.in", "io2.in", "io3.in", "io4.in", "io5.in" }));
            Assert.That(Names(PortKind.TachoInput), Is.EqualTo(new[] { "out3.tach", "out4.tach", "out5.tach" }));
            Assert.That(Names(PortKind.AnalogInput), Is.EqualTo(new[] { "temp0", "temp1", "temp2" }));
            Assert.That(Names(PortKind.Output), Does.Contain("out0").And.Contain("out8").And.Contain("io0.out"));
            Assert.That(ports.Single(p => p.Name == "io0.out").Aliases, Is.EqualTo(new[] { "uart0.tx" }));
        });
    }

    [Test]
    public void PortsAreInNumberOrder()
    {
        string[] outputs = [.. BoardPorts.Of(BoardTypes.Exp3hc).Where(p => p.Kind == PortKind.Output && p.Name.StartsWith("out")).Select(p => p.Name)];

        Assert.That(outputs, Is.EqualTo(new[] { "out0", "out1", "out2", "out3", "out4", "out5", "out6", "out7", "out8" }));
    }

    [Test]
    public void EveryBoardsPortsCanBeDescribed([ValueSource(typeof(BoardTypes), nameof(BoardTypes.All))] BoardType type) =>
        Assert.That(BoardPorts.Of(type), Is.Not.Empty);
}
