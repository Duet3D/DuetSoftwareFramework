using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Motion.Kinematics;
using NUnit.Framework;
using Code = DuetAPI.Commands.Code;

namespace UnitTests.Motion;

/// <summary>
/// Which geometry M669 K selects
/// </summary>
/// <remarks>
/// K is RepRapFirmware's <c>KinematicsType</c> number or the geometry's name as a quoted string, and
/// both forms select the same geometry
/// </remarks>
[TestFixture]
public class KinematicsSelectionTests
{
    private static KinematicsEngine Select(string codeText)
    {
        bool seen = false;
        KinematicsEngine engine = KinematicsConfigurator.Apply(KinematicsFactory.Create(KinematicsName.Cartesian),
                                                               KinematicsAxes.NewMoveWithAxes(), new Code(codeText), ref seen);
        Assert.That(seen, Is.True, $"'{codeText}' selected nothing");
        return engine;
    }

    [TestCase(0, KinematicsName.Cartesian)]
    [TestCase(1, KinematicsName.CoreXY)]
    [TestCase(2, KinematicsName.CoreXZ)]
    [TestCase(3, KinematicsName.LinearDelta)]
    [TestCase(4, KinematicsName.Scara)]
    [TestCase(5, KinematicsName.CoreXYU)]
    [TestCase(6, KinematicsName.Hangprinter)]
    [TestCase(7, KinematicsName.Polar)]
    [TestCase(8, KinematicsName.CoreXYUV)]
    [TestCase(9, KinematicsName.FiveBarScara)]
    [TestCase(10, KinematicsName.RotaryDelta)]
    [TestCase(11, KinematicsName.MarkForged)]
    public void ANumberSelectsTheGeometryRepRapFirmwareNumbersSo(int number, KinematicsName expected)
        => Assert.That(Select($"M669 K{number}").Kind, Is.EqualTo(expected));

    [TestCase("cartesian", KinematicsName.Cartesian)]
    [TestCase("corexy", KinematicsName.CoreXY)]
    [TestCase("CoreXY", KinematicsName.CoreXY)]
    [TestCase("corexz", KinematicsName.CoreXZ)]
    [TestCase("delta", KinematicsName.LinearDelta)]
    [TestCase("scara", KinematicsName.Scara)]
    [TestCase("corexyu", KinematicsName.CoreXYU)]
    [TestCase("hangprinter", KinematicsName.Hangprinter)]
    [TestCase("polar", KinematicsName.Polar)]
    [TestCase("corexyuv", KinematicsName.CoreXYUV)]
    [TestCase("fivebarscara", KinematicsName.FiveBarScara)]
    [TestCase("Rotary delta", KinematicsName.RotaryDelta)]
    [TestCase("markforged", KinematicsName.MarkForged)]
    public void ANameSelectsTheGeometryItNames(string name, KinematicsName expected)
        => Assert.That(Select($"M669 K\"{name}\"").Kind, Is.EqualTo(expected));

    [TestCase("M669 K12", "Unknown kinematics type 12")]
    [TestCase("M669 K-1", "Unknown kinematics type -1")]
    [TestCase("M669 K\"cubic\"", "Unknown kinematics type \"cubic\"")]
    public void AKThatNamesNoGeometryIsRefused(string codeText, string message)
    {
        bool seen = false;
        GCodeException? error = Assert.Throws<GCodeException>(() => KinematicsConfigurator.Apply(
            KinematicsFactory.Create(KinematicsName.Cartesian), KinematicsAxes.NewMoveWithAxes(), new Code(codeText), ref seen));
        Assert.That(error!.Message, Is.EqualTo(message));
    }
}
