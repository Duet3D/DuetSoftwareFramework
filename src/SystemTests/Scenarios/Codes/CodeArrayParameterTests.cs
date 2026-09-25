using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios.Codes;

/// <summary>
/// The rules every code that reads a list of values is held to: how many it may carry, what one
/// value stands for, and what happens to a code that carries the wrong number
/// </summary>
/// <remarks>
/// RepRapFirmware reads a list into an array of the size the caller gives and refuses the value that
/// would not fit as it reaches it, so a line naming more drives than the machine has is refused
/// rather than applied to the drives it does reach (StringParser::CheckArrayLength). The bench half
/// of what <c>lib/DuetRegressionTesting</c> records for <c>M906 E400:410:420:430</c> and for
/// <c>M17 E</c>, both of which this side used to take
/// </remarks>
[TestFixture]
public class CodeArrayParameterTests : BenchFixture
{
    /// <summary>
    /// Three extruders on the rig, so a list can name more values than the machine has drives
    /// </summary>
    /// <remarks>
    /// The rig's own mapping gives E one driver, and the drivers this adds are already enabled by
    /// the preamble's M569 lines
    /// </remarks>
    private const string ThreeExtruders = "M584 E2.0:1.3:1.4";

    /// <summary>
    /// A list naming more extruders than the machine has refuses the code where the extra value
    /// stands, leaves every current as it was and tells no board
    /// </summary>
    /// <remarks>
    /// The reference records "Error: at column 19: M906: array too long for parameter 'E'" for this
    /// line against three extruders. The column is where the fourth value begins, because
    /// RepRapFirmware refuses it as it reaches it rather than after reading the list
    /// </remarks>
    [Test]
    public async Task M906RefusesMoreValuesThanThereAreExtruders()
    {
        await using JobBench bench = await DriversBench.StartAsync(configExtra: ThreeExtruders + "\nM906 E300:310:200");

        bench.CanMaster.ClearCapture();
        string reply = await bench.Host.ExecuteCodeAsync("M906 E400:410:420:430");

        Assert.Multiple(async () =>
        {
            Assert.That(reply.TrimEnd(), Is.EqualTo("Error: at column 19: M906: array too long for parameter 'E'"));
            Assert.That(bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestMotorCurrents>(), Is.Empty,
                        "a refused code sends nothing, where taking the first three would have sent three requests");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].Current), Is.EqualTo(300));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[1].Current), Is.EqualTo(310));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[2].Current), Is.EqualTo(200),
                        "every extruder keeps the current it had, including the ones the list did reach");
        });
    }

    /// <summary>
    /// One value applies to every extruder, which is how nearly every configuration is written
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>GetFloatArray(eVals, eCount, true)</c>: with one value given it fills the
    /// rest of the array with it before the handler sees it (GCodeBuffer.cpp)
    /// </remarks>
    [Test]
    public async Task M906SpreadsOneValueOverEveryExtruder()
    {
        await using JobBench bench = await DriversBench.StartAsync(configExtra: ThreeExtruders);

        bench.CanMaster.ClearCapture();
        Assert.That((await bench.Host.ExecuteCodeAsync("M906 E700")).Trim(), Is.Empty);

        Assert.That(bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestMotorCurrents>(), Has.Count.EqualTo(3),
                    "every extruder is set, so every one of their drivers is told");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].Current), Is.EqualTo(700));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[1].Current), Is.EqualTo(700));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[2].Current), Is.EqualTo(700));
        });
    }

    /// <summary>
    /// A list of as many values as there are extruders is taken positionally, and a shorter one
    /// leaves the extruders it does not reach alone
    /// </summary>
    [Test]
    public async Task M906TakesOneValuePerExtruderInTurn()
    {
        await using JobBench bench = await DriversBench.StartAsync(configExtra: ThreeExtruders + "\nM906 E300:310:200");

        bench.CanMaster.ClearCapture();
        Assert.That((await bench.Host.ExecuteCodeAsync("M906 E400:410:420")).Trim(), Is.Empty);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].Current), Is.EqualTo(400));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[1].Current), Is.EqualTo(410));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[2].Current), Is.EqualTo(420));
        });

        Assert.That((await bench.Host.ExecuteCodeAsync("M906 E500:510")).Trim(), Is.Empty);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].Current), Is.EqualTo(500));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[1].Current), Is.EqualTo(510));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[2].Current), Is.EqualTo(420),
                        "the third extruder was never named, so it keeps what it had");
        });
    }

    /// <summary>
    /// A machine with no extruders refuses the letter itself, before the value it does not carry
    /// </summary>
    /// <remarks>
    /// <c>testcases/motion/error-missing-parameters-motion-mcodes.yaml</c> records
    /// "Error: at column 6: M17: array too long for parameter 'E'" for a bare E, because
    /// RepRapFirmware checks the length before reading each value and there is room for none
    /// </remarks>
    [Test]
    public async Task ACodeReadingNoExtrudersRefusesTheLetterAsTooLong()
    {
        await using JobBench bench = await DriversBench.StartAsync(configExtra: "M584 E");

        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders.Count), Is.Zero,
                    "a bare E in M584 leaves the machine with no extruders");

        Assert.Multiple(async () =>
        {
            Assert.That((await bench.Host.ExecuteCodeAsync("M17 E")).TrimEnd(),
                        Is.EqualTo("Error: at column 6: M17: array too long for parameter 'E'"));
            Assert.That((await bench.Host.ExecuteCodeAsync("M906 E800")).TrimEnd(),
                        Is.EqualTo("Error: at column 7: M906: array too long for parameter 'E'"),
                        "and so is a value there is no drive to apply it to");
        });
    }

    /// <summary>
    /// M557 describes each axis of its grid with a minimum and a maximum, and refuses any other
    /// number of values
    /// </summary>
    /// <remarks>
    /// RepRapFirmware reads the ranges with <c>TryGetFloatArray(letter, 2, ...)</c>, which refuses a
    /// list that stops short as well as one that runs long (GCodes6.cpp <c>DefineGrid</c>)
    /// </remarks>
    [Test]
    public async Task M557RefusesAnAxisRangeThatIsNotAPair()
    {
        await using JobBench bench = await DriversBench.StartAsync();

        Assert.Multiple(async () =>
        {
            Assert.That((await bench.Host.ExecuteCodeAsync("M557 X20 Y20:180")).TrimEnd(),
                        Is.EqualTo("Error: M557: Wrong number of values in array, expected 2"),
                        "one value is half a range");
            Assert.That((await bench.Host.ExecuteCodeAsync("M557 X20:180:200 Y20:180")).TrimEnd(),
                        Is.EqualTo("Error: at column 14: M557: array too long for parameter 'X'"),
                        "and three is refused where the third stands, the length being checked as it is read");
        });

        Assert.That((await bench.Host.ExecuteCodeAsync("M557 X20:180 Y20:180 S40")).Trim(), Is.Empty,
                    "a pair for each axis is what it asks for");
    }

    /// <summary>
    /// One point count stands for both axes of the grid
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>TryGetUIArray('P', 2, numPoints, seenP, true)</c>, which pads before it
    /// checks the count, so a single value satisfies a caller that needs two
    /// </remarks>
    [Test]
    public async Task M557SpreadsOnePointCountOverBothAxes()
    {
        await using JobBench bench = await DriversBench.StartAsync();

        Assert.That((await bench.Host.ExecuteCodeAsync("M557 X20:180 Y20:180 P5")).Trim(), Is.Empty);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Compensation.ProbeGrid.Spacings[0]),
                        Is.EqualTo(40.0f).Within(0.05f), "five points across 160mm is a spacing of 40");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Compensation.ProbeGrid.Spacings[1]),
                        Is.EqualTo(40.0f).Within(0.05f), "and the one count applies to the other axis too");
        });
    }
}
