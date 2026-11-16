using System.Threading.Tasks;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Drivers;

/// <summary>
/// M569.9, the driver sense resistor and maximum current
/// </summary>
/// <remarks>
/// Only the STM32 port of RepRapFirmware implements it; on Duet 3 it reaches the default case of RRF CanInterface::ConfigureRemoteDriver, which is errorNotSupported: the driver is parsed, then the code is declined as an error with nothing sent
/// </remarks>
[TestFixture]
[GCode("M569.9")]
public class M569Point9Tests : BenchFixture
{
    /// <summary>The forms <c>m569-9-sense-resistor.yaml</c> sends, on the rig's drivers</summary>
    private static readonly string[] Forms =
    [
        $"M569.9 P{DriversRig.MainDriver(0)}",
        $"M569.9 P{DriversRig.MainDriver(0)} R0.075 S4.4",
    ];

    /// <summary>
    /// Every form is declined as an error, not the warning a code nothing knows gets, and nothing reaches the bus
    /// </summary>
    /// <remarks><c>m569-9-sense-resistor.yaml</c></remarks>
    [TestCaseSource(nameof(Forms))]
    [Category("KnownGap")]
    public async Task M569Point9IsNotSupported(string code)
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        string reply = await bench.Host.ExecuteCodeAsync(code);
        Assert.Multiple(() =>
        {
            Assert.That(reply.TrimEnd(), Is.EqualTo("Error: M569.9: Command is not supported"));
            Assert.That(bench.CanMaster.SbcPackets(SbcRequest.SendCANMessage), Is.Empty, "and no board is asked anything");
        });
    }

    /// <summary>
    /// M569.9 without P is refused as every M569 sub-code is, before it is declined
    /// </summary>
    /// <remarks><c>error-missing-parameters-drivers-m569.yaml</c> (RRF GCodes3.cpp ConfigureDriver <c>gb.MustSee('P')</c>)</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M569Point9WithoutPIsRefused()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync("M569.9")).TrimEnd(), Is.EqualTo("Error: M569.9: missing parameter 'P'"));
    }
}
