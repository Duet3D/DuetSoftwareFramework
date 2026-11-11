using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Files.FileSetup;

namespace SystemTests.Scenarios.Codes.Files;

/// <summary>M37, simulating a file and the simulation mode</summary>
[TestFixture]
[GCode("M37")]
public class M37Tests : BenchFixture
{
    /// <summary>
    /// M37 simulates a whole file: the machine reports simulating while it runs, the job is marked
    /// as simulated afterwards, and the simulated time is written back into the file
    /// </summary>
    /// <remarks>
    /// GCodes3.cpp <c>SimulateFile</c> selects the file and starts printing it in simulation mode,
    /// which RepRap.cpp <c>GetStatusIndex</c> reports as "simulating" while the print monitor is
    /// printing. GCodes.cpp <c>StopPrint</c> then records the simulated time into the file unless F0
    /// asked it not to (<c>MassStorage::RecordSimulationTime</c>, the "; Simulated print time"
    /// comment FileInfoParser.h names), and the file info parser reads it back as
    /// <c>job.file.simulatedTime</c>
    /// </remarks>
    [Test]
    [Category("LongRunning")]
    public async Task M37SimulatesAFile()
    {
        // Slow padding, so the simulation is still running when the model is read
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd =>
            sd.WriteGCode("sim.gcode", "G90\nG1 X10 Y10 F3000\n" + JobDriving.FillerMoves(pairs: 60, feed: 600))).StartAsync();

        // F0 leaves the file alone, so this is the simulation without the write-back the next test
        // covers on its own
        Task<string> simulation = bench.Host.ExecuteCodeAsync("M37 P\"0:/gcodes/sim.gcode\" F0");
        await bench.Host.WaitForStatusAsync(MachineStatus.Simulating);
        Assert.That(await bench.Host.ReadModelAsync(model => model.Job.File.FileName), Is.EqualTo("0:/gcodes/sim.gcode"),
                    "the simulated file is the one being printed (PrintMonitor.cpp fileName)");

        await simulation;
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle, timeoutMs: 60_000);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await SettledValueAsync(bench.Host, model => model.Job.LastFileSimulated, true), Is.True,
                        "the finished job was a simulation (job.lastFileSimulated)");
            Assert.That(await SettledValueAsync(bench.Host, model => model.Job.LastFileName, "0:/gcodes/sim.gcode"), Is.EqualTo("0:/gcodes/sim.gcode"),
                        "the simulated file becomes job.lastFileName (PrintMonitor.cpp lastFileName)");
            Assert.That(await SettledNumberAsync(bench.Host, model => model.Job.LastDuration), Is.GreaterThanOrEqualTo(0),
                        "job.lastDuration holds the simulated duration (GCodes.cpp StopPrint, lastDuration)");
        });
    }

    /// <summary>
    /// M37 without F0 writes the simulated time back into the file it simulated, where the file info
    /// parser reads it as job.file.simulatedTime
    /// </summary>
    /// <remarks>
    /// GCodes.cpp <c>StopPrint</c> calls <c>MassStorage::RecordSimulationTime</c> when the
    /// simulation completes normally and F did not say otherwise (GCodes2.cpp case 37), appending
    /// or replacing the "; Simulated print time" comment FileInfoParser.h names, which
    /// FileInfoParser.cpp reads back into <c>printingFileInfo.simulatedTime</c>
    /// </remarks>
    [Test]
    public async Task M37WritesTheSimulatedTimeIntoTheFile()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd =>
            sd.WriteGCode("sim.gcode", "G90\nG1 X10 Y10 F3000\n" + JobDriving.FillerMoves(pairs: 30, feed: 600))).StartAsync();

        Task<string> simulation = bench.Host.ExecuteCodeAsync("M37 P\"0:/gcodes/sim.gcode\"");
        await bench.Host.WaitForStatusAsync(MachineStatus.Simulating);
        await simulation;
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle, timeoutMs: 20_000);

        Assert.That(await File.ReadAllTextAsync(Physical(bench, "gcodes", "sim.gcode")),
                    Does.Contain("; Simulated print time"),
                    "the simulated time is appended to the file (MassStorage::RecordSimulationTime)");

        await bench.Host.ExecuteCodeAsync("M23 \"0:/gcodes/sim.gcode\"");
        Assert.That(await SettledNumberAsync(bench.Host, model => model.Job.File.SimulatedTime), Is.GreaterThanOrEqualTo(0),
                    "the recorded time is read back as job.file.simulatedTime (PrintMonitor.cpp simulatedTime)");
    }

    /// <summary>
    /// M37 with no P parameter reports the simulation mode, and M37 S turns it on and off
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp case 37: with neither P nor S it reports
    /// "Simulation mode: &lt;on|off&gt;, move time: &lt;n&gt; sec, other time: &lt;n&gt; sec", and S
    /// changes the mode through GCodes3.cpp <c>ChangeSimulationMode</c>. The mode alone does not make
    /// the machine report "simulating": RepRap.cpp <c>GetStatusIndex</c> needs a file being printed
    /// as well
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M37ReportsAndChangesTheSimulationMode()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed);

        Assert.That(await bench.Host.ExecuteCodeAsync("M37"), Does.Contain("Simulation mode: off"),
                    "M37 with no parameters reports the simulation mode (GCodes2.cpp case 37)");

        await bench.Host.ExecuteCodeAsync("M37 S1");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.ExecuteCodeAsync("M37"), Does.Contain("Simulation mode: on"),
                        "M37 S1 turns simulation on (GCodes3.cpp ChangeSimulationMode)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.Status), Is.EqualTo(MachineStatus.Idle),
                        "simulation mode with no file being printed is still idle (RepRap.cpp GetStatusIndex)");
        });

        await bench.Host.ExecuteCodeAsync("M37 S0");
        Assert.That(await bench.Host.ExecuteCodeAsync("M37"), Does.Contain("Simulation mode: off"),
                    "M37 S0 turns simulation off again (GCodes3.cpp ChangeSimulationMode)");
    }
}
