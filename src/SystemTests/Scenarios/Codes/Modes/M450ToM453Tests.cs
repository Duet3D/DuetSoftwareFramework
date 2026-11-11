using System;
using System.Globalization;
using System.Threading.Tasks;
using DuetControlServer.Heat;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Tools.ToolSetup;

namespace SystemTests.Scenarios.Codes.Modes;

/// <summary>M450 to M453, the machine mode, and the laser power M3 and M5 set in laser mode</summary>
[TestFixture]
[GCode("M450", "M451", "M452", "M453", "M3", "M5")]
public class M450ToM453Tests : BenchFixture
{
    /// <summary>
    /// Poll an object model field until it reads the expected number, failing with the last reading
    /// on timeout. Used for values that settle after the code's reply
    /// </summary>
    private static async Task WaitForNumberAsync(DcsTestHost host,
                                                 Func<DuetControlServer.Model.ObjectModel, double?> read,
                                                 double expected, string what, int timeoutMs = 10_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        double? value;
        do
        {
            value = await host.ReadModelAsync(read);
            if (value is double reading && Math.Abs(reading - expected) < 1e-3)
            {
                return;
            }
            await Task.Delay(25);
        }
        while (DateTime.UtcNow < deadline);
        Assert.Fail($"{what}: reads '{value}', expected {expected}");
    }

    /// <summary>
    /// M451 and M453 switch state.machineMode between FFF and CNC, and M450 reports the mode in
    /// RepRapFirmware's format
    /// </summary>
    /// <remarks>
    /// The M450, M451 and M453 cases in GCodes2.cpp set machineType, reported through
    /// GCodes::GetMachineModeString (GCodes.cpp) as "FFF"/"CNC"/"Laser"; M450's reply is
    /// "PrinterMode:%s"
    /// </remarks>
    [Test]
    public async Task MachineModeCodesSwitchAndReport()
    {
        await using Bench bench = await Bench.StartAsync();
        DcsTestHost host = bench.Host;

        Assert.Multiple(async () =>
        {
            Assert.That(await host.ReadModelAsync(model => model.State.MachineMode), Is.EqualTo(MachineMode.FFF), "the machine starts in FFF mode (RRF MachineType::fff default)");
            Assert.That((await host.ExecuteCodeAsync("M450")).Trim(), Is.EqualTo("PrinterMode:FFF"), "M450 reports the mode (RRF GCodes2.cpp M450 case)");
        });

        await host.ExecuteCodeAsync("M453");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.ReadModelAsync(model => model.State.MachineMode), Is.EqualTo(MachineMode.CNC), "M453 sets state.machineMode to CNC (RRF GCodes2.cpp M453 case)");
            Assert.That((await host.ExecuteCodeAsync("M450")).Trim(), Is.EqualTo("PrinterMode:CNC"), "M450 reports the new mode (RRF GCodes2.cpp M450 case)");
        });

        await host.ExecuteCodeAsync("M451");
        Assert.That(await host.ReadModelAsync(model => model.State.MachineMode), Is.EqualTo(MachineMode.FFF),
                    "M451 sets state.machineMode back to FFF (RRF GCodes2.cpp M451 case)");
    }

    /// <summary>
    /// In laser mode M3 S sets the beam power for subsequent moves rather than driving a spindle:
    /// state.laserPwm changes only when a move is built, and M5 turns the beam off
    /// </summary>
    /// <remarks>
    /// The M452 case in GCodes2.cpp (S1 makes the power sticky across moves); the laser branch of
    /// the M3/M5 cases stores the power in the movement state's laser pixel data, and
    /// DoStraightMove copies it into the move (GCodes.cpp), where state.laserPwm reads it scaled
    /// to 0..1 (GCodes::GetLaserPwm, GCodes.h, and RepRap.cpp state table)
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task LaserModeAppliesM3PowerToMoves()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M452 S1");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.ReadModelAsync(model => model.State.MachineMode), Is.EqualTo(MachineMode.Laser), "M452 sets state.machineMode to Laser (RRF GCodes2.cpp M452 case)");
            Assert.That(await EvaluateNumberAsync(host, model => model.State.LaserPwm), Is.EqualTo(0).Within(1e-3), "laser mode starts with the beam off (RRF GCodes::GetLaserPwm)");
        });

        await host.ExecuteCodeAsync("M3 S255");
        Assert.That(await EvaluateNumberAsync(host, model => model.State.LaserPwm), Is.EqualTo(0).Within(1e-3),
                    "M3 S alone does not fire the beam; the power applies to moves (RRF M3 laser branch, DoStraightMove)");

        await host.ExecuteCodeAsync("G91");
        await host.ExecuteCodeAsync("G1 X1 F6000");
        await WaitForNumberAsync(host, model => model.State.LaserPwm, 1.0,
                                 "the move carries M3's sticky power, 255 of 255 (RRF DoStraightMove, GCodes::GetLaserPwm)");

        await host.ExecuteCodeAsync("M5");
        await host.ExecuteCodeAsync("G1 X1 F6000");
        await WaitForNumberAsync(host, model => model.State.LaserPwm, 0.0,
                                 "M5 clears the laser power for later moves (RRF M5 laser branch)");
    }
}
