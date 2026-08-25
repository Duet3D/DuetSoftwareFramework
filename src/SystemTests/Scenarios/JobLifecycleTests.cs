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
}
