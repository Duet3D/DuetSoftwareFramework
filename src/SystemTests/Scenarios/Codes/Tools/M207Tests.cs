using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Tools;

/// <summary>M207, a tool's firmware retraction settings</summary>
/// <remarks>
/// GCodes2.cpp case 207 and Tool::SetFirmwareRetraction (Tool.cpp). M207 only changes tools[].retraction;
/// nothing goes to an expansion board, so every scenario also checks that no CAN message is sent
/// </remarks>
[TestFixture]
[GCode("M207")]
public class M207Tests : BenchFixture
{
    /// <summary>Two tools with no drives, heaters or fans, which is all M207 needs</summary>
    private const string TwoToolsConfig = """
        M563 P0 S"First"
        M563 P1 S"Second"
        """;

    /// <summary>One tool with no drives, heaters or fans</summary>
    private const string OneToolConfig = "M563 P0 S\"Only\"";

    /// <summary>RRF DefaultRetractLength, in mm</summary>
    private const float DefaultLength = 2.0f;

    /// <summary>RRF DefaultRetractSpeed (1000 mm/min), in mm/s as tools[].retraction reports it</summary>
    private const float DefaultSpeed = 1000.0f / 60.0f;

    /// <summary>RRF MinRetractSpeed (60 mm/min), in mm/s</summary>
    private const float MinSpeed = 1.0f;

    /// <summary>Tolerance for the speeds, which RRF converts from mm/min</summary>
    private const float SpeedTolerance = 1e-3f;

    /// <summary>A tool's retraction settings, read in one go</summary>
    public readonly record struct Retraction(float Length, float ExtraRestart, float Speed, float UnretractSpeed, float ZHop);

    private static Task<Retraction> ReadRetractionAsync(Bench bench, int tool)
        => bench.Host.ReadModelAsync(model =>
        {
            ToolRetraction retraction = model.Tools[tool]!.Retraction;
            return new Retraction(retraction.Length, retraction.ExtraRestart, retraction.Speed, retraction.UnretractSpeed, retraction.ZHop);
        });

    private static void AssertRetraction(Retraction actual, Retraction expected, string what)
    {
        Assert.That(actual.Length, Is.EqualTo(expected.Length).Within(1e-4f), $"{what}: retraction.length");
        Assert.That(actual.ExtraRestart, Is.EqualTo(expected.ExtraRestart).Within(1e-4f), $"{what}: retraction.extraRestart");
        Assert.That(actual.Speed, Is.EqualTo(expected.Speed).Within(SpeedTolerance), $"{what}: retraction.speed");
        Assert.That(actual.UnretractSpeed, Is.EqualTo(expected.UnretractSpeed).Within(SpeedTolerance), $"{what}: retraction.unretractSpeed");
        Assert.That(actual.ZHop, Is.EqualTo(expected.ZHop).Within(1e-4f), $"{what}: retraction.zHop");
    }

    private static readonly Retraction Defaults = new(DefaultLength, 0.0f, DefaultSpeed, DefaultSpeed, 0.0f);

    /// <summary>Sets every field of tool 0 away from its default, so a field a later M207 omits can be seen to keep its value</summary>
    private const string ConfigureTool0 = "M207 P0 S3 R0.5 F1800 T900 Z0.25";

    /// <summary>What <see cref="ConfigureTool0"/> sets</summary>
    private static readonly Retraction Configured = new(3.0f, 0.5f, 30.0f, 15.0f, 0.25f);

    /// <summary>The non-empty lines of a reply</summary>
    private static string[] Lines(string reply) => reply.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>M207 P sets every parameter of that tool only, P0 included: P0 names a tool, it does not mean "no P"</summary>
    /// <remarks>
    /// S, R and Z are stored as given; F and T are mm/min, reported in mm/s (Tool objectModelTable
    /// InverseConvertSpeedToMmPerSec). The other tool keeps the defaults from Tool::Create.
    /// Regression: m207-set-retract-parameters, where M207 P1 changes only tools[1].retraction
    /// </remarks>
    [Category("KnownGap")]
    [TestCase(0, TestName = "M207 P0 sets only tool 0")]
    [TestCase(1, TestName = "M207 P1 sets only tool 1")]
    public async Task M207PSetsOnlyThatTool(int tool)
    {
        await using Bench bench = await Bench.StartAsync(TwoToolsConfig);

        int other = 1 - tool;
        string reply = (await bench.Host.ExecuteCodeAsync($"M207 P{tool} S3.5 R0.25 F2400 T1200 Z0.4")).Trim();
        Retraction set = await ReadRetractionAsync(bench, tool), untouched = await ReadRetractionAsync(bench, other);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Is.Empty, "setting succeeds silently");
            AssertRetraction(set, new Retraction(3.5f, 0.25f, 40.0f, 20.0f, 0.4f), $"tools[{tool}]");
            AssertRetraction(untouched, Defaults, $"tools[{other}] is untouched");
            Assert.That(bench.CanMaster.SbcPackets(SbcRequest.SendCANMessage), Is.Empty, "nothing is sent");
        });
    }

    /// <summary>M207 without P sets every tool</summary>
    /// <remarks>
    /// Tool::SetAllToolsFirmwareRetraction calls SetFirmwareRetraction on each tool in turn.
    /// Regression: m207-set-retract-parameters, where M207 without P sets both tools
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M207WithoutPSetsEveryTool()
    {
        await using Bench bench = await Bench.StartAsync(TwoToolsConfig);

        string reply = (await bench.Host.ExecuteCodeAsync("M207 S1.5 Z0.2")).Trim();
        Retraction first = await ReadRetractionAsync(bench, 0), second = await ReadRetractionAsync(bench, 1);
        Retraction expected = Defaults with { Length = 1.5f, ZHop = 0.2f };
        Assert.Multiple(() =>
        {
            Assert.That(reply, Is.Empty, "setting succeeds silently");
            AssertRetraction(first, expected, "tools[0]");
            AssertRetraction(second, expected, "tools[1]");
            Assert.That(bench.CanMaster.SbcPackets(SbcRequest.SendCANMessage), Is.Empty, "nothing is sent");
        });
    }

    /// <summary>Out-of-range values are clamped rather than refused</summary>
    /// <remarks>
    /// Tool::SetFirmwareRetraction: S is max(S, 0); R, read after S whatever the order on the line, is
    /// max(R, -length); F and T are at least MinRetractSpeed
    /// </remarks>
    [Category("KnownGap")]
    [TestCase("M207 S-1", 0.0f, 0.0f, DefaultSpeed, DefaultSpeed, TestName = "M207 S below zero is clamped to zero")]
    [TestCase("M207 S1 R-5", 1.0f, -1.0f, DefaultSpeed, DefaultSpeed, TestName = "M207 R is clamped to minus the length the same line sets")]
    [TestCase("M207 R-5 S1", 1.0f, -1.0f, DefaultSpeed, DefaultSpeed, TestName = "M207 R is clamped against S even when R comes first")]
    [TestCase("M207 R-5", DefaultLength, -DefaultLength, DefaultSpeed, DefaultSpeed, TestName = "M207 R alone is clamped to minus the current length")]
    [TestCase("M207 F30", DefaultLength, 0.0f, MinSpeed, MinSpeed, TestName = "M207 F below 60 mm per min is clamped for both speeds")]
    [TestCase("M207 T30", DefaultLength, 0.0f, DefaultSpeed, MinSpeed, TestName = "M207 T below 60 mm per min is clamped")]
    public async Task M207ClampsOutOfRangeValues(string code, float length, float extraRestart, float speed, float unretractSpeed)
    {
        await using Bench bench = await Bench.StartAsync(OneToolConfig);

        string reply = (await bench.Host.ExecuteCodeAsync(code)).Trim();
        Retraction retraction = await ReadRetractionAsync(bench, 0);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Is.Empty, "clamping is silent");
            AssertRetraction(retraction, new Retraction(length, extraRestart, speed, unretractSpeed, 0.0f), "tools[0]");
            Assert.That(bench.CanMaster.SbcPackets(SbcRequest.SendCANMessage), Is.Empty, "nothing is sent");
        });
    }

    /// <summary>F sets both speeds and T then overrides the unretract speed, whatever their order on the line</summary>
    /// <remarks>Tool::SetFirmwareRetraction reads F before T</remarks>
    [Category("KnownGap")]
    [TestCase("M207 F1200", 20.0f, 20.0f, TestName = "M207 F alone sets both speeds")]
    [TestCase("M207 F1200 T600", 20.0f, 10.0f, TestName = "M207 T after F sets the unretract speed")]
    [TestCase("M207 T600 F1200", 20.0f, 10.0f, TestName = "M207 T before F still sets the unretract speed")]
    public async Task M207FThenT(string code, float speed, float unretractSpeed)
    {
        await using Bench bench = await Bench.StartAsync(OneToolConfig);

        string reply = (await bench.Host.ExecuteCodeAsync(code)).Trim();
        Retraction retraction = await ReadRetractionAsync(bench, 0);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Is.Empty, "setting succeeds silently");
            AssertRetraction(retraction, Defaults with { Speed = speed, UnretractSpeed = unretractSpeed }, "tools[0]");
            Assert.That(bench.CanMaster.SbcPackets(SbcRequest.SendCANMessage), Is.Empty, "nothing is sent");
        });
    }

    /// <summary>G20 does not scale any M207 parameter, set or reported</summary>
    /// <remarks>
    /// Tool::SetFirmwareRetraction reads S and R with GetFValue, Z with GetNonNegativeFValue and F and T with
    /// GetSpeedFromMm, none of which applies the distance scale (GCodeBuffer.cpp)
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M207IgnoresInches()
    {
        await using Bench bench = await Bench.StartAsync(OneToolConfig);

        await bench.Host.ExecuteCodeAsync("G20");
        string set = (await bench.Host.ExecuteCodeAsync("M207 S1 R0.5 F600 T300 Z0.1")).Trim();
        Retraction retraction = await ReadRetractionAsync(bench, 0);
        string report = (await bench.Host.ExecuteCodeAsync("M207")).Trim();
        Assert.Multiple(() =>
        {
            Assert.That(set, Is.Empty, "setting succeeds silently");
            AssertRetraction(retraction, new Retraction(1.0f, 0.5f, 10.0f, 5.0f, 0.1f), "tools[0] in mm and mm/s");
            Assert.That(report, Is.EqualTo("Tool 0 retract/reprime: length 1.00/1.50mm, speed 10.0/5.0mm/sec, Z hop 0.10mm"), "the report is in mm too");
        });
    }

    /// <summary>M207 with no parameter to set reports every tool, one line each</summary>
    /// <remarks>
    /// Tool::SetFirmwareRetraction with nothing seen: "Tool %u retract/reprime: length %.2f/%.2fmm,
    /// speed %.1f/%.1fmm/sec, Z hop %.2fmm", the reprime length being length + extraRestart.
    /// Regression: m207-set-retract-parameters, whose bare M207 replies one such line per tool
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M207ReportsEveryTool()
    {
        await using Bench bench = await Bench.StartAsync(TwoToolsConfig);

        await bench.Host.ExecuteCodeAsync("M207 P1 S3 R0.5 F1800 T900 Z0.25");
        string reply = await bench.Host.ExecuteCodeAsync("M207");
        Assert.Multiple(() =>
        {
            Assert.That(Lines(reply), Is.EqualTo(new[]
            {
                "Tool 0 retract/reprime: length 2.00/2.00mm, speed 16.7/16.7mm/sec, Z hop 0.00mm",
                "Tool 1 retract/reprime: length 3.00/3.50mm, speed 30.0/15.0mm/sec, Z hop 0.25mm"
            }), "one line per tool, in tool order");
            Assert.That(bench.CanMaster.SbcPackets(SbcRequest.SendCANMessage), Is.Empty, "nothing is sent");
        });
    }

    /// <summary>M207 P with no parameter to set reports only that tool</summary>
    [Category("KnownGap")]
    [Test]
    public async Task M207PReportsOneTool()
    {
        await using Bench bench = await Bench.StartAsync(TwoToolsConfig);

        await bench.Host.ExecuteCodeAsync("M207 P1 S3 R0.5 F1800 T900 Z0.25");
        string reply = await bench.Host.ExecuteCodeAsync("M207 P1");
        Assert.That(Lines(reply), Is.EqualTo(new[] { "Tool 1 retract/reprime: length 3.00/3.50mm, speed 30.0/15.0mm/sec, Z hop 0.25mm" }), "only tool 1 is reported");
    }

    /// <summary>With no tools, M207 succeeds and has nothing to report</summary>
    /// <remarks>Tool::SetAllToolsFirmwareRetraction loops over an empty tool list and returns ok</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M207WithNoToolsReportsNothing()
    {
        await using Bench bench = await Bench.StartAsync();

        string report = (await bench.Host.ExecuteCodeAsync("M207")).Trim();
        string set = (await bench.Host.ExecuteCodeAsync("M207 S3")).Trim();
        Assert.Multiple(() =>
        {
            Assert.That(report, Is.Empty, "the report is empty");
            Assert.That(set, Is.Empty, "setting with no tools succeeds silently");
            Assert.That(bench.CanMaster.SbcPackets(SbcRequest.SendCANMessage), Is.Empty, "nothing is sent");
        });
    }

    /// <summary>M207 P naming a tool that does not exist is an error and changes nothing</summary>
    /// <remarks>GCodes2.cpp case 207: "Tool %u does not exist", before any parameter is read</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M207PNamingAbsentToolFails()
    {
        await using Bench bench = await Bench.StartAsync(TwoToolsConfig);

        string reply = (await bench.Host.ExecuteCodeAsync("M207 P5 S3")).Trim();
        Retraction first = await ReadRetractionAsync(bench, 0), second = await ReadRetractionAsync(bench, 1);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error:"));
            AssertRetraction(first, Defaults, "tools[0] is untouched");
            AssertRetraction(second, Defaults, "tools[1] is untouched");
            Assert.That(bench.CanMaster.SbcPackets(SbcRequest.SendCANMessage), Is.Empty, "nothing is sent");
        });
    }


    /// <summary>The cases of <see cref="M207ChangesOnlyTheFieldsItNames"/>: one parameter each, from <see cref="Configured"/></summary>
    private static IEnumerable<TestCaseData> OneParameterCases()
    {
        yield return new TestCaseData("M207 S4", Configured with { Length = 4.0f }).SetName("M207 S alone changes only the length");
        yield return new TestCaseData("M207 R-0.2", Configured with { ExtraRestart = -0.2f }).SetName("M207 R alone changes only the extra restart");
        yield return new TestCaseData("M207 F2400", Configured with { Speed = 40.0f, UnretractSpeed = 40.0f }).SetName("M207 F alone changes only the two speeds");
        yield return new TestCaseData("M207 T600", Configured with { UnretractSpeed = 10.0f }).SetName("M207 T alone changes only the unretract speed");
        yield return new TestCaseData("M207 Z0.6", Configured with { ZHop = 0.6f }).SetName("M207 Z alone changes only the Z hop");
    }

    /// <summary>A parameter M207 does not name keeps the value it had, and setting anything replies nothing</summary>
    /// <remarks>Tool::SetFirmwareRetraction writes only the fields whose letters it has seen, and reports only when it has seen none</remarks>
    [Category("KnownGap")]
    [TestCaseSource(nameof(OneParameterCases))]
    public async Task M207ChangesOnlyTheFieldsItNames(string code, Retraction expected)
    {
        await using Bench bench = await Bench.StartAsync(OneToolConfig, ConfigureTool0);

        string reply = (await bench.Host.ExecuteCodeAsync(code)).Trim();
        Retraction retraction = await ReadRetractionAsync(bench, 0);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Is.Empty, "setting succeeds silently, without a report");
            AssertRetraction(retraction, expected, "tools[0]");
            Assert.That(bench.CanMaster.SbcPackets(SbcRequest.SendCANMessage), Is.Empty, "nothing is sent");
        });
    }

    /// <summary>F given after a separate T sets the unretract speed again</summary>
    /// <remarks>Tool::SetFirmwareRetraction: F sets unRetractSpeed as well as retractSpeed</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M207FAfterASeparateTOverwritesTheUnretractSpeed()
    {
        await using Bench bench = await Bench.StartAsync(OneToolConfig);

        await bench.Host.ExecuteCodeAsync("M207 T600");
        string reply = (await bench.Host.ExecuteCodeAsync("M207 F1200")).Trim();
        Retraction retraction = await ReadRetractionAsync(bench, 0);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Is.Empty, "setting succeeds silently");
            AssertRetraction(retraction, Defaults with { Speed = 20.0f, UnretractSpeed = 20.0f }, "tools[0]: F replaced the earlier T");
        });
    }

    /// <summary>Tools with gaps between their numbers are set and reported by number, in number order</summary>
    /// <remarks>
    /// Tool::AddTool keeps the tool list sorted by number, whatever order M563 created them in, and M207 walks
    /// that list. The model's tools[] has empty entries for the numbers in between
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M207FollowsToolNumbersWithGaps()
    {
        await using Bench bench = await Bench.StartAsync("""
            M563 P3 S"Three"
            M563 P1 S"One"
            """);

        string setOne = (await bench.Host.ExecuteCodeAsync("M207 P3 S3")).Trim();
        Retraction one = await ReadRetractionAsync(bench, 1), three = await ReadRetractionAsync(bench, 3);
        string report = await bench.Host.ExecuteCodeAsync("M207");
        string setAll = (await bench.Host.ExecuteCodeAsync("M207 Z0.3")).Trim();
        Retraction oneAfter = await ReadRetractionAsync(bench, 1), threeAfter = await ReadRetractionAsync(bench, 3);
        Assert.Multiple(() =>
        {
            Assert.That(setOne, Is.Empty, "M207 P3 succeeds silently");
            AssertRetraction(three, Defaults with { Length = 3.0f }, "M207 P3 sets tools[3]");
            AssertRetraction(one, Defaults, "and leaves tools[1] untouched");
            Assert.That(Lines(report), Is.EqualTo(new[]
            {
                "Tool 1 retract/reprime: length 2.00/2.00mm, speed 16.7/16.7mm/sec, Z hop 0.00mm",
                "Tool 3 retract/reprime: length 3.00/3.00mm, speed 16.7/16.7mm/sec, Z hop 0.00mm"
            }), "the report lists tool 1 before tool 3");
            Assert.That(setAll, Is.Empty, "M207 without P succeeds silently");
            AssertRetraction(oneAfter, Defaults with { ZHop = 0.3f }, "M207 without P sets tools[1]");
            AssertRetraction(threeAfter, Defaults with { Length = 3.0f, ZHop = 0.3f }, "and tools[3]");
        });
    }

    /// <summary>M207 P naming a tool that does not exist is an error when it reports, as when it sets</summary>
    /// <remarks>GCodes2.cpp case 207 looks the tool up before SetFirmwareRetraction decides whether to report</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M207PReportingAbsentToolFails()
    {
        await using Bench bench = await Bench.StartAsync(TwoToolsConfig);

        string reply = (await bench.Host.ExecuteCodeAsync("M207 P5")).Trim();
        Assert.That(reply, Does.StartWith("Error:"));
    }

    /// <summary>A negative P is not a number M207 accepts, so it is refused before any tool is looked up</summary>
    /// <remarks>StrToU32's NumericConverter refuses a leading '-', so CheckNumberFound throws "expected number after 'P'"</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M207RefusesANegativeP()
    {
        await using Bench bench = await Bench.StartAsync(TwoToolsConfig);

        string reply = (await bench.Host.ExecuteCodeAsync("M207 P-1 S3")).Trim();
        Retraction first = await ReadRetractionAsync(bench, 0), second = await ReadRetractionAsync(bench, 1);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error:"));
            AssertRetraction(first, Defaults, "tools[0] is untouched");
            AssertRetraction(second, Defaults, "tools[1] is untouched");
        });
    }

    /// <summary>M207 has no upper limits: large values are stored as given</summary>
    /// <remarks>Tool::SetFirmwareRetraction clamps only from below</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M207StoresLargeValuesAsGiven()
    {
        await using Bench bench = await Bench.StartAsync(OneToolConfig);

        string reply = (await bench.Host.ExecuteCodeAsync("M207 S50 R20 F30000 T30000 Z10")).Trim();
        Retraction retraction = await ReadRetractionAsync(bench, 0);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Is.Empty, "setting succeeds silently");
            AssertRetraction(retraction, new Retraction(50.0f, 20.0f, 500.0f, 500.0f, 10.0f), "tools[0]");
        });
    }

    /// <summary>A letter with no number after it is an error and changes nothing</summary>
    /// <remarks>
    /// StringParser::CheckNumberFound, reached from ReadFloatValue for S, R, F, T and Z and from ReadUIValue for P:
    /// "expected number after '%c'", at the column after the letter
    /// </remarks>
    [Category("KnownGap")]
    [TestCase('S')]
    [TestCase('R')]
    [TestCase('F')]
    [TestCase('T')]
    [TestCase('Z')]
    [TestCase('P')]
    public async Task M207RefusesALetterWithoutANumber(char letter)
    {
        await using Bench bench = await Bench.StartAsync(OneToolConfig);

        string reply = (await bench.Host.ExecuteCodeAsync($"M207 {letter}")).Trim();
        Retraction retraction = await ReadRetractionAsync(bench, 0);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error:"));
            AssertRetraction(retraction, Defaults, "tools[0] is untouched");
        });
    }

    /// <summary>M207 in a file being simulated still sets the values, so the simulation sees the Z hop the print would</summary>
    /// <remarks>GCodes2.cpp HandleMcode skips most M-codes while simulating, but not M207</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M207RunsWhileSimulating()
    {
        await using Bench bench = await Bench.With(OneToolConfig)
                                             .WithSd(sd => sd.WriteGCode("sim.gcode", "M207 S7.5 Z0.4\n"))
                                             .StartAsync();

        await bench.Host.ExecuteCodeAsync("M37 P\"0:/gcodes/sim.gcode\" F0");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle, timeoutMs: 60_000);
        AssertRetraction(await ReadRetractionAsync(bench, 0), Defaults with { Length = 7.5f, ZHop = 0.4f }, "tools[0] after the simulation");
    }

    /// <summary>M207 does not wait for motion: it completes while a move queued before it is still in progress</summary>
    /// <remarks>GCodes2.cpp case 207 takes no movement lock and does not wait for standstill</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M207DoesNotWaitForMotion()
    {
        using SteppedTimeline timeline = new();
        await using Bench bench = await Bench.With(Machines.Xye, OneToolConfig, Machines.Xye.Homed)
                                             .SteppedBy(timeline)
                                             .StartAsync();

        await timeline.WhileRunningAsync(() => bench.Host.ExecuteCodeAsync("G90"));
        await bench.Host.ExecuteCodeAsync("G1 X20 F600");

        Task<string> m207 = bench.Host.ExecuteCodeAsync("M207 S7.5");
        bool completed = await Task.WhenAny(m207, Task.Delay(TimeSpan.FromSeconds(5))) == m207;
        Retraction retraction = await ReadRetractionAsync(bench, 0);

        // Let the move finish, which also releases an M207 that wrongly waits for it
        await timeline.WhileRunningAsync(() => bench.Host.ExecuteCodeAsync("M400"));
        string reply = (await m207).Trim();
        Assert.Multiple(() =>
        {
            Assert.That(completed, Is.True, "M207 completes while the X move is held by the frozen timeline");
            Assert.That(reply, Is.Empty, "setting succeeds silently");
            AssertRetraction(retraction, Defaults with { Length = 7.5f }, "tools[0] is set before the move finishes");
        });
    }
}
