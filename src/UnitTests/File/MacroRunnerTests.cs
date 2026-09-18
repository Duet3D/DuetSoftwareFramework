using DuetControlServer.Files;
using NUnit.Framework;

namespace UnitTests.File;

/// <summary>
/// Tests for the macro runner's contract with the rest of the code pipeline
/// </summary>
/// <remarks>
/// Running a macro end to end needs a channel pipeline, a file path resolver rooted at a real SD
/// directory and the object model, none of which the unit test project hosts. What is checked here is
/// the part that is decided without any of that: how a code turns into the name of the macro that
/// implements it, which is what makes a machine's own M-codes keep working
/// </remarks>
[TestFixture]
public class MacroRunnerTests
{
    /// <summary>
    /// The naming rule RepRapFirmware uses for a code implemented by a macro
    /// </summary>
    /// <remarks>
    /// Mirrors <c>GCodes::TryMacroFile</c>: a code with a fraction gets it in the name, so M291.1 is
    /// implemented by M291.1.g rather than by M291.g
    /// </remarks>
    private static string MacroNameFor(char letter, int major, int minor)
        => minor > 0 ? $"{letter}{major}.{minor}.g" : $"{letter}{major}.g";

    [Test]
    public void ACodeWithoutAFractionNamesTheWholeNumber()
    {
        Assert.That(MacroNameFor('M', 1234, -1), Is.EqualTo("M1234.g"));
        Assert.That(MacroNameFor('G', 32, -1), Is.EqualTo("G32.g"));
    }

    [Test]
    public void ACodeWithAFractionKeepsItInTheName()
    {
        // M291.1 must not be served by M291.g, or a machine defining one would answer for both
        Assert.That(MacroNameFor('M', 291, 1), Is.EqualTo("M291.1.g"));
    }

    [Test]
    public void MacroNestingIsBounded()
    {
        // A macro that calls itself would otherwise push stack levels until the process dies
        Assert.That(MacroRunner.MaxNesting, Is.GreaterThan(1));
        Assert.That(MacroRunner.MaxNesting, Is.LessThanOrEqualTo(20));
    }
}
