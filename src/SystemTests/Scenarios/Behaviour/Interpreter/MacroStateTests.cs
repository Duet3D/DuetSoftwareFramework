using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Motion.InputChannels;

namespace SystemTests.Scenarios.Behaviour.Interpreter;

/// <summary>
/// The interpreter state config.g and its macros leave behind, and the state a macro keeps to itself
/// </summary>
[TestFixture]
public class MacroStateTests : BenchFixture
{
    /// <summary>
    /// The startup files - config.g, config-override.g (M501) and dsf-config.g - and any macro they
    /// call retain their interpreter state when they finish, rather than having it restored the way
    /// an ordinary macro does, so an M83 or G91 in config.g is the machine's setting afterwards
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>CheckFinishedRunningConfigFile</c> copies config.g's state up rather than
    /// discarding it with the frame, "so that M83 etc. in nested files don't get forgotten"
    /// (GCodes.cpp). DuetControlServer keeps the same rule for all three startup files and the macros
    /// they call: the restore <see cref="AMacroDoesNotLeakItsInterpreterStateToTheCaller"/> checks is
    /// skipped while a startup file is running. State is retained on the channel the file ran on -
    /// the Trigger channel for config.g at boot, and the invoking channel for M501 and dsf-config.g
    /// </remarks>
    [Test]
    public async Task ConfigRetainsInterpreterState()
    {
        // config.g sets the feed rate (before G93, so F is stored not read as a duration), the
        // relativity and the time mode itself, and a sub-macro sets the rest; all must remain
        await using Bench bench = await Bench.With(Machines.Xye, """
                G1 F1234
                G91
                G93
                M98 P"0:/macros/config-sub.g"
                """).WithSd(sd =>
            {
                sd.WriteMacro("config-sub.g", "M83\nG20\n");
                sd.WriteMacro("override-sub.g", "G91\nG20\n");
                sd.WriteMacro("dsfconfig-sub.g", "M83\nG20\n");
                sd.WriteSys("config-override.g", "G1 F2345\nM98 P\"0:/macros/override-sub.g\"\n");
                sd.WriteSys("dsf-config.g", "G91\nM98 P\"0:/macros/dsfconfig-sub.g\"\n");
            }).StartAsync();

        // config.g ran at boot on the Trigger channel; its state and its sub-macro's state stand
        int trigger = await ChannelInputIndexAsync(bench.Host, CodeChannel.Trigger);
        InputChannel triggerChannel = (await bench.Host.ReadModelAsync(model => model.Inputs[trigger]))!;
        Assert.Multiple(() =>
        {
            Assert.That(triggerChannel.AxesRelative, Is.True, "config.g's G91 is retained");
            Assert.That(triggerChannel.InverseTimeMode, Is.True, "config.g's G93 is retained");
            Assert.That(triggerChannel.FeedRate, Is.EqualTo(1234.0).Within(1e-3), "config.g's feed rate is retained");
            Assert.That(triggerChannel.DrivesRelative, Is.True, "the M83 in config.g's sub-macro is retained");
            Assert.That(triggerChannel.DistanceUnit, Is.EqualTo(DistanceUnit.Inch), "the G20 in config.g's sub-macro is retained");
        });

        int http = await HttpInputIndexAsync(bench.Host);
        InputChannel httpChannel = (await bench.Host.ReadModelAsync(model => model.Inputs[http]))!;

        // config-override.g via M501, on the HTTP channel: its feed rate and its sub-macro's state
        // stand, unlike an ordinary M98 macro
        await bench.Host.ExecuteCodeAsync("M501");
        Assert.Multiple(() =>
        {
            Assert.That(httpChannel.FeedRate, Is.EqualTo(2345.0).Within(1e-3), "config-override.g's feed rate is retained");
            Assert.That(httpChannel.AxesRelative, Is.True, "the G91 in config-override.g's sub-macro is retained");
            Assert.That(httpChannel.DistanceUnit, Is.EqualTo(DistanceUnit.Inch), "the G20 in config-override.g's sub-macro is retained");
        });

        // Put the channel back to the defaults so what dsf-config.g leaves is unmistakably its own
        await bench.Host.ExecuteCodeAsync("G90");
        await bench.Host.ExecuteCodeAsync("M82");
        await bench.Host.ExecuteCodeAsync("G21");

        // dsf-config.g run as a macro on the HTTP channel; its filename is what marks it, so it and
        // its sub-macro retain their state the same way
        await bench.Host.ExecuteCodeAsync("M98 P\"dsf-config.g\"");
        Assert.Multiple(() =>
        {
            Assert.That(httpChannel.AxesRelative, Is.True, "dsf-config.g's G91 is retained");
            Assert.That(httpChannel.DrivesRelative, Is.True, "the M83 in dsf-config.g's sub-macro is retained");
            Assert.That(httpChannel.DistanceUnit, Is.EqualTo(DistanceUnit.Inch), "the G20 in dsf-config.g's sub-macro is retained");
        });
    }

    /// <summary>
    /// A macro carries its own copy of the caller's interpreter state, so a modal code inside it does
    /// not leak out: when the macro ends the caller's feed rate, relativity, units and time mode are
    /// as they were before the M98
    /// </summary>
    /// <remarks>
    /// <para>
    /// RepRapFirmware pushes a <c>GCodeMachineState</c> for every macro frame
    /// (<c>GCodeMachineState.cpp</c> copy constructor) and pops it on return, so the modal state a
    /// macro changes is discarded with the frame. The full set of modal fields the copy constructor
    /// stacks is: <c>axesRelative</c> (G90/G91), <c>drivesRelative</c> (M82/M83), <c>usingInches</c>
    /// (G20/G21), <c>inverseTimeMode</c> (G93/G94), <c>feedRate</c> (F), <c>selectedPlane</c>
    /// (G17/G18/G19), <c>volumetricExtrusion</c> (M200, reset to off in the child) and
    /// <c>compatibility</c> (M555).
    /// </para>
    /// <para>
    /// This exercises the five DuetControlServer implements a code for today, all published on
    /// <c>inputs[]</c>. The remaining three - <c>selectedPlane</c>, <c>volumetric</c> and
    /// <c>compatibility</c> - have their <c>inputs[]</c> fields but no code sets them yet, so they
    /// are added here when G17/G18/G19, M200 and M555 land.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AMacroDoesNotLeakItsInterpreterStateToTheCaller()
    {
        // G94 before the F so the feed rate registers rather than being read as an inverse time,
        // then each modal set to the opposite of what the caller holds
        await using Bench bench = await Bench.With().WithSd(sd =>
            sd.WriteMacro("modal-state.g", """
                G90
                M82
                G21
                G94
                G1 F6000
                """)).StartAsync();
        int http = await HttpInputIndexAsync(bench.Host);
        InputChannel? httpChannel = await bench.Host.ReadModelAsync(model => model.Inputs[http]);

        // The caller's state: the feed rate first, while still in the default non-inverse-time mode,
        // so F is stored rather than taken as a duration
        await bench.Host.ExecuteCodeAsync("G1 F1000");
        await bench.Host.ExecuteCodeAsync("G91");
        await bench.Host.ExecuteCodeAsync("M83");
        await bench.Host.ExecuteCodeAsync("G20");
        await bench.Host.ExecuteCodeAsync("G93");
        double callerFeedRate = httpChannel!.FeedRate;

        // The macro changes every one of them; RRF discards those changes with the macro frame
        await bench.Host.ExecuteCodeAsync("M98 P\"0:/macros/modal-state.g\"");

        Assert.Multiple(() =>
        {
            Assert.That(httpChannel.AxesRelative, Is.True,
                        "G90 in the macro does not clear the caller's G91 (RRF GCodeMachineState axesRelative)");
            Assert.That(httpChannel.DrivesRelative, Is.True,
                        "M82 in the macro does not clear the caller's M83 (RRF GCodeMachineState drivesRelative)");
            Assert.That(httpChannel.DistanceUnit, Is.EqualTo(DistanceUnit.Inch),
                        "G21 in the macro does not undo the caller's G20 (RRF GCodeMachineState usingInches)");
            Assert.That(httpChannel.InverseTimeMode, Is.True,
                        "G94 in the macro does not clear the caller's G93 (RRF GCodeMachineState inverseTimeMode)");
            Assert.That(httpChannel.FeedRate, Is.EqualTo(callerFeedRate).Within(1e-3),
                        "the F in the macro does not change the caller's feed rate (RRF GCodeMachineState feedRate)");
        });
    }
}
