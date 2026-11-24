using System.Threading.Tasks;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Drivers;

/// <summary>
/// M569.8, reading motor force over the secondary CAN bus
/// </summary>
/// <remarks>
/// Like M569.3 it needs ODrives on the second CAN bus and Hangprinter kinematics (RRF CanInterface.cpp ConfigureRemoteDriver case 8), so every other machine takes RepRapFirmware's errorNotSupported path: the drivers are parsed, then the code is declined as an error with nothing sent
/// </remarks>
[TestFixture]
[GCode("M569.8")]
public class M569Point8Tests : BenchFixture
{
    /// <summary>The forms <c>m569-8-read-motor-force.yaml</c> sends, on the rig's drivers</summary>
    private static readonly string[] Forms =
    [
        $"M569.8 P{DriversRig.MainDriver(0)}",
        $"M569.8 P{DriversRig.MainDriver(0)}:{DriversRig.ClosedLoopDriver(0)}",
    ];

    /// <summary>
    /// Every form is declined as an error, not the warning a code nothing knows gets, and nothing reaches the bus
    /// </summary>
    /// <remarks><c>m569-8-read-motor-force.yaml</c></remarks>
    [TestCaseSource(nameof(Forms))]
    [Category("KnownGap")]
    public async Task M569Point8IsNotSupported(string code)
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        string reply = await bench.Host.ExecuteCodeAsync(code);
        Assert.Multiple(() =>
        {
            Assert.That(reply.TrimEnd(), Is.EqualTo("Error: M569.8: Command is not supported"));
            Assert.That(bench.CanMaster.SbcPackets(SbcRequest.SendCANMessage), Is.Empty, "and no board is asked anything");
        });
    }

    /// <summary>
    /// M569.8 without P is refused as every M569 sub-code is, before it is declined
    /// </summary>
    /// <remarks><c>error-missing-parameters-drivers-m569.yaml</c> (RRF GCodes3.cpp ConfigureDriver <c>gb.MustSee('P')</c>)</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M569Point8WithoutPIsRefused()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync("M569.8")).TrimEnd(), Is.EqualTo("Error: M569.8: missing parameter 'P'"));
    }
}
