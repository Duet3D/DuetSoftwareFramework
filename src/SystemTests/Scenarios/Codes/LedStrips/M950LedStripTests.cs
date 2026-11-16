using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.LedStrips;

/// <summary>M950 E, creating, reconfiguring, reporting and deleting an LED strip</summary>
[TestFixture]
[GCode("M950")]
public class M950LedStripTests : BenchFixture
{
    /// <summary>The output a NeoPixel strip is driven from</summary>
    private static readonly string StripPort = Expansion.GpioOut(Machines.DriverBoard, 5);

    /// <summary>A three-LED NeoPixel strip as strip 0</summary>
    private static readonly string NeoPixelConfig = $"M950 E0 C\"{StripPort}\" T1 Q3000000 U3";

    /// <summary>
    /// M950 E creates a NeoPixel strip on the board its port names, passing the code's parameters on
    /// and recording the strip in ledStrips[]
    /// </summary>
    /// <remarks>
    /// RemoteLedStrip::Configure sends the code as written (PopulateFromCommand) and keeps U and K for
    /// the object model; bit 0 of the reply's extra byte says the board drives the strip by DMA, so a
    /// zero means it bit-bangs it and motion has to stop (MustStopMovement). The regression reference
    /// m950-led-strip-neopixel records <c>{C: "io5.out", E: 0, Q: 3000000, T: 1, U: 3}</c>, extra 0, and
    /// <c>ledStrips[0]</c> with board 3, colorOrder 5, maxLeds 3, pin "io5.out", stopMovement true and
    /// type NeoPixel_RGB
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950ECreatesANeoPixelStrip()
    {
        await using Bench bench = await Bench.With(NeoPixelConfig).KeepingConfigTraffic().StartAsync();

        (byte board, CanMessageM950Led sent) = bench.CanMaster.LastCanMessage<CanMessageM950Led>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard), "the strip is created on the board its port names");
            Assert.That(sent.E, Is.EqualTo(0));
            Assert.That(sent.C, Is.EqualTo("io5.out"), "the pin as the board knows it");
            Assert.That(sent.T, Is.EqualTo(1));
            Assert.That(sent.Q, Is.EqualTo(3000000));
            Assert.That(sent.U, Is.EqualTo(3));
            Assert.That(sent.K, Is.Null, "and only what the code gave");
        });

        LedStrip? strip = await bench.Host.ReadModelAsync(model => model.LedStrips[0]);
        Assert.Multiple(() =>
        {
            Assert.That(strip?.Board, Is.EqualTo(Machines.DriverBoard), "ledStrips[0].board");
            Assert.That(strip?.Pin, Is.EqualTo("io5.out"), "ledStrips[0].pin is the pin without the board address");
            Assert.That(strip?.Type, Is.EqualTo(LedStripType.NeoPixel_RGB));
            Assert.That(strip?.ColorOrder, Is.EqualTo(LedStripColorOrder.GRB), "a NeoPixel strip defaults to GRB");
            Assert.That(strip?.MaxLeds, Is.EqualTo(3), "U sets ledStrips[0].maxLeds");
            Assert.That(strip?.StopMovement, Is.True, "a board that does not drive it by DMA bit-bangs it");
            Assert.That(strip?.Frequency, Is.EqualTo(3000000), "Q is kept in ledStrips[0].frequency (DSF addition)");
        });
    }

    /// <summary>
    /// A DotStar strip takes its colour order from K, and a board that drives it by DMA does not need
    /// motion stopped to update it
    /// </summary>
    /// <remarks>
    /// The regression reference m950-led-strip-dotstar records <c>{C: "led", E: 1, K: 2, T: 0, U: 30}</c>,
    /// extra 1, and <c>ledStrips[1]</c> with colorOrder 2, maxLeds 30, stopMovement false and type
    /// DotStar, with <c>ledStrips[0]</c> null
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950ECreatesADotStarStrip()
    {
        await using Bench bench = await Bench.StartAsync();
        bench.CanMaster.ReportWith<CanMessageM950Led>(string.Empty, extra: 1);

        string reply = await bench.Host.ExecuteCodeAsync($"M950 E1 C\"{Machines.DriverBoard}.led\" T0 K2 U30");
        LedStrip? strip = await bench.Host.ReadModelAsync(model => model.LedStrips[1]);
        Assert.Multiple(async () =>
        {
            Assert.That(reply.Trim(), Is.Empty);
            Assert.That(strip?.Type, Is.EqualTo(LedStripType.DotStar));
            Assert.That(strip?.ColorOrder, Is.EqualTo(LedStripColorOrder.RGB), "K2 sets ledStrips[1].colorOrder");
            Assert.That(strip?.MaxLeds, Is.EqualTo(30));
            Assert.That(strip?.StopMovement, Is.False, "the board drives it by DMA");
            Assert.That(strip?.Pin, Is.EqualTo("led"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.LedStrips[0]), Is.Null, "ledStrips[0] is an empty slot");
        });
    }

    /// <summary>
    /// A strip created with neither T nor U is an RGB NeoPixel strip of 60 LEDs, and a DotStar strip
    /// without K is BGR
    /// </summary>
    /// <remarks>DefaultLedStripType and DefaultMaxLedsPerStrip (RRF3Common.h), and the LedStripBase constructor</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950EAppliesTheDefaults()
    {
        await using Bench bench = await Bench.StartAsync(
            $"M950 E0 C\"{StripPort}\"\nM950 E1 C\"{Machines.DriverBoard}.led\" T0");

        Assert.Multiple(async () =>
        {
            LedStrip? neoPixel = await bench.Host.ReadModelAsync(model => model.LedStrips[0]);
            Assert.That(neoPixel?.Type, Is.EqualTo(LedStripType.NeoPixel_RGB));
            Assert.That(neoPixel?.MaxLeds, Is.EqualTo(LedStrip.DefaultMaxLeds));
            Assert.That(neoPixel?.ColorOrder, Is.EqualTo(LedStripColorOrder.GRB));
            Assert.That(neoPixel?.Frequency, Is.Null, "with no Q the board uses its default for the type");
            Assert.That((await bench.Host.ReadModelAsync(model => model.LedStrips[1]))?.ColorOrder,
                        Is.EqualTo(LedStripColorOrder.BGR), "a DotStar strip defaults to BGR");
        });
    }

    /// <summary>
    /// M950 E C"nil" tells the strip's board to release it and empties ledStrips[]
    /// </summary>
    /// <remarks><c>~RemoteLedStrip</c> sends E and C"nil" and ignores the reply</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950EDeletesAStripWithCNil()
    {
        await using Bench bench = await Bench.StartAsync(NeoPixelConfig);
        bench.CanMaster.ReportWith<CanMessageM950Led>("Unsupported LED strip type", CodeResult.Error);

        string reply = await bench.Host.ExecuteCodeAsync("M950 E0 C\"nil\"");
        (byte board, CanMessageM950Led sent) = bench.CanMaster.LastCanMessage<CanMessageM950Led>();
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply.Trim(), Is.Empty, "the delete succeeds whatever the board says");
            Assert.That(board, Is.EqualTo(Machines.DriverBoard));
            Assert.That(sent.E, Is.EqualTo(0));
            Assert.That(sent.C, Is.EqualTo("nil"));
            Assert.That(sent.T, Is.Null, "and nothing else");
            Assert.That(bench.CanMaster.CanMessages<CanMessageM950Led>(), Has.Count.EqualTo(1));
            Assert.That(await bench.Host.ReadModelAsync(model => model.LedStrips.ElementAtOrDefault(0)), Is.Null);
        });
    }

    /// <summary>
    /// M950 E with neither C, T nor U changes the minor parameters of an existing strip on its board
    /// and records them once the board has taken them
    /// </summary>
    /// <remarks>LedStripManager::CreateStrip calls Configure with no pin name, and RemoteLedStrip passes the code on</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950EChangesTheColourOrderOfAnExistingStrip()
    {
        await using Bench bench = await Bench.StartAsync(NeoPixelConfig);

        await bench.Host.ExecuteCodeAsync("M950 E0 K2");
        (_, CanMessageM950Led sent) = bench.CanMaster.LastCanMessage<CanMessageM950Led>();
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(sent.E, Is.EqualTo(0));
            Assert.That(sent.K, Is.EqualTo(2));
            Assert.That(sent.C, Is.Null, "the pin is left alone");
            Assert.That(await bench.Host.ReadModelAsync(model => model.LedStrips[0]!.ColorOrder),
                        Is.EqualTo(LedStripColorOrder.RGB));
            Assert.That(await bench.Host.ReadModelAsync(model => model.LedStrips[0]!.MaxLeds), Is.EqualTo(3),
                        "and the rest of the strip is kept");
        });

        bench.CanMaster.ReportWith<CanMessageM950Led>("Invalid color order K=9", CodeResult.Warning);
        string warned = await bench.Host.ExecuteCodeAsync("M950 E0 K9");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(warned, Does.StartWith("Warning:"), "the board warns about a colour order there is not");
            Assert.That(await bench.Host.ReadModelAsync(model => model.LedStrips[0]!.ColorOrder),
                        Is.EqualTo(LedStripColorOrder.RGB), "and ledStrips[0].colorOrder keeps the last valid one");
        });
    }

    /// <summary>
    /// A strip that does not exist cannot be reported or reconfigured
    /// </summary>
    /// <remarks>LedStripManager::CreateStrip: <c>LED strip 2 does not exist</c></remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950ERefusesAStripThatDoesNotExist()
    {
        await using Bench bench = await Bench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync("M950 E2");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error:").And.Contain("LED strip 2 does not exist"));
            Assert.That(bench.CanMaster.CanMessages<CanMessageM950Led>(), Is.Empty);
        });
    }

    /// <summary>
    /// M950 E with no C and no strip asks the board to report it and answers with what it says
    /// </summary>
    [Test]
    [Category("KnownGap")]
    public async Task M950EAsksTheBoardToReportAStrip()
    {
        const string report = "Strip 0 is NeoPixel_RGB on pin 1.io5.out";
        await using Bench bench = await Bench.StartAsync(NeoPixelConfig);
        bench.CanMaster.ReportWith<CanMessageM950Led>(report);

        string reply = await bench.Host.ExecuteCodeAsync("M950 E0");
        (_, CanMessageM950Led sent) = bench.CanMaster.LastCanMessage<CanMessageM950Led>();
        Assert.Multiple(() =>
        {
            Assert.That(reply.Trim(), Is.EqualTo(report));
            Assert.That(sent.E, Is.EqualTo(0));
            Assert.That(sent.C, Is.Null);
            Assert.That(sent.K, Is.Null);
        });
    }

    /// <summary>A board that refuses the strip leaves no strip</summary>
    [Test]
    [Category("KnownGap")]
    public async Task ARefusedStripLeavesNoStrip()
    {
        await using Bench bench = await Bench.StartAsync();
        bench.CanMaster.ReportWith<CanMessageM950Led>("Unsupported LED strip type", CodeResult.Error);

        string reply = await bench.Host.ExecuteCodeAsync($"M950 E0 C\"{StripPort}\" T0");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:").And.Contain("Unsupported LED strip type"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.LedStrips.ElementAtOrDefault(0)), Is.Null);
        });
    }

    /// <summary>Lines M950 E refuses, and what it says</summary>
    private static readonly object[] BadParameters =
    [
        new object[] { $"E{CanLimits.MaxLedStrips} C\"{StripPort}\"", "parameter 'E' too high" },
        new object[] { $"E0 C\"{StripPort}\" T3", "parameter 'T' too high" },
        new object[] { "E0 U10", "missing parameter 'C'" }
    ];

    /// <summary>
    /// The guards on M950 E: the strip number, the type and the port a new strip needs
    /// </summary>
    /// <remarks>
    /// LedStripManager::CreateStrip: <c>GetLimitedUIValue('E', MaxLedStrips)</c>,
    /// <c>GetLimitedUIValue('T', 3)</c> and <c>MustSee('C')</c> once T or U says a strip is being created
    /// </remarks>
    [TestCaseSource(nameof(BadParameters))]
    [Category("KnownGap")]
    public async Task M950ERefusesBadParameters(string parameters, string error)
    {
        await using Bench bench = await Bench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M950 {parameters}");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error:").And.Contain(error));
            Assert.That(bench.CanMaster.CanMessages<CanMessageM950Led>(), Is.Empty);
        });
    }
}
