using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios;

/// <summary>
/// The job lifecycle end to end against the real motion engine: select a file, print, pause with a
/// real feedhold, run the pause macros, resume. What docs/devel/JOB_LIFECYCLE.md marks for hardware
/// verification gets its first automated home here
/// </summary>
[TestFixture]
public class JobLifecycleTests : SystemTests.Host.BenchFixture
{
    private static string SocketPath() => Path.Combine(Path.GetTempPath(), $"dsf-fake-{Guid.NewGuid():N}.sock");

    private const string OneAxisConfig = """
        M953
        M569 P1.0 S1
        M584 X1.0
        M92 X80
        M906 X800
        M201 X500
        M203 X6000
        M566 X900
        M208 X0:200
        M564 H0 S0
        G92 X0
        """;

    /// <summary>
    /// Enough moves that the job file is still being read whenever the pause lands: a G-code
    /// completes once its move is queued, so a file with fewer moves than the ring holds is
    /// finished the moment it starts
    /// </summary>
    private static string JobFile()
    {
        System.Text.StringBuilder job = new("G91\n");
        for (int i = 0; i < 200; i++)
        {
            job.Append(i % 2 == 0 ? "G1 X5 F1200\n" : "G1 X-5 F1200\n");
        }
        return job.ToString();
    }

    [Test]
    public async Task PauseAndResumeMidJob()
    {
        using ScriptedCanMaster fake = new(SocketPath());
        fake.AckCanRequestsWithStandardReplies();
        await using DcsTestHost host = await DcsTestHost.StartAsync(fake, prepareSd: sd =>
        {
            sd.WriteSys("config.g", OneAxisConfig + "\nglobal pauseRan = 0\nglobal resumeRan = 0\n" + DcsTestHost.ConfigDoneMarker);
            sd.WriteSys("pause.g", "set global.pauseRan = global.pauseRan + 1\n");
            sd.WriteSys("resume.g", "set global.resumeRan = global.resumeRan + 1\n");
            sd.WriteGCode("job.gcode", JobFile());
        });
        await host.WaitForConfigDoneAsync();

        // Select and start the job; motion follows over the link
        await host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await host.WaitForStatusAsync(MachineStatus.Processing);
        await fake.WaitForSbcPacketAsync(SbcRequest.ScheduleMove);

        // Pause mid-move: a real feedhold through the real motion engine
        await host.ExecuteCodeAsync("M25");
        await host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.That((await host.ExecuteCodeAsync("echo global.pauseRan")).Trim(), Is.EqualTo("1"),
                    "pause.g ran once");

        // Resume: the restore point replays and the job carries on
        int movesBeforeResume = fake.SbcPackets(SbcRequest.ScheduleMove).Count;
        await host.ExecuteCodeAsync("M24");
        await host.WaitForStatusAsync(MachineStatus.Processing);
        await fake.WaitUntilAsync(() => fake.SbcPackets(SbcRequest.ScheduleMove).Count > movesBeforeResume,
                                  what: "motion resuming after M24");
        Assert.That((await host.ExecuteCodeAsync("echo global.resumeRan")).Trim(), Is.EqualTo("1"),
                    "resume.g ran once");

        // And the job can be brought to an orderly stop from the paused state
        await host.ExecuteCodeAsync("M25");
        await host.WaitForStatusAsync(MachineStatus.Paused);
        await host.ExecuteCodeAsync("M0");
        await host.WaitForStatusAsync(MachineStatus.Idle);
    }

    /// <summary>
    /// A job whose moves are computed from local variables declared at the top of the file, and
    /// short enough that the reader finishes the whole file while the machine is still executing
    /// the moves - the deferred M106/M107 keep the job alive in that window, exactly as in the
    /// reported failure. A pause then rewinds the file into the middle of the moves, and the
    /// resume must still be able to evaluate <c>var.distance</c> on the lines it re-reads:
    /// RepRapFirmware keeps a job file's local variables until the print stops, not until the
    /// reader happens to reach the end of the file
    /// </summary>
    [Test]
    public async Task PauseAndResumeWithLocalVariables()
    {
        using ScriptedCanMaster fake = new(SocketPath());
        fake.AckCanRequestsWithStandardReplies();
        await using DcsTestHost host = await DcsTestHost.StartAsync(fake, prepareSd: sd =>
        {
            sd.WriteSys("config.g", OneAxisConfig + "\nM950 F0 C\"1.out1\"\n" + DcsTestHost.ConfigDoneMarker);
            sd.WriteGCode("vars.gcode", """
                var speed = 20   ; mm/s
                var time = 0.5   ; sec
                var distance = var.speed * var.time

                G91
                G1 X{var.distance} F{var.speed * 60}
                G1 X{var.distance}
                M106 S1
                G1 X{var.distance}
                G1 X{var.distance}
                M107
                """);
        });
        await host.WaitForConfigDoneAsync();

        // Start the job; the reader runs to the end of the file long before the four 0.5 s moves
        // have been executed, and the M106/M107 defer behind the moves they follow
        await host.ExecuteCodeAsync("M32 \"0:/gcodes/vars.gcode\"");
        await host.WaitForStatusAsync(MachineStatus.Processing);
        await fake.WaitForSbcPacketAsync(SbcRequest.ScheduleMove);

        // Pause while the machine is still working through the queued moves
        await host.ExecuteCodeAsync("M25");
        await host.WaitForStatusAsync(MachineStatus.Paused);

        // Resume: the rewound G1 X{var.distance} lines must still see the variable
        await host.ExecuteCodeAsync("M24");
        await host.WaitForStatusAsync(MachineStatus.Idle, timeoutMs: 30_000);

        bool aborted;
        using (await host.Model.AccessReadOnlyAsync(CancellationToken.None))
        {
            aborted = host.Model.Job.LastFileAborted;
        }
        Assert.That(aborted, Is.False, "the job ran to completion");

        // Every move ran exactly once: four relative moves of 10 mm each
        string position = (await host.ExecuteCodeAsync("echo move.axes[0].userPosition")).Trim();
        Assert.That(float.Parse(position, System.Globalization.CultureInfo.InvariantCulture),
                    Is.EqualTo(40.0f).Within(0.01f), "all four moves completed");
    }
}
