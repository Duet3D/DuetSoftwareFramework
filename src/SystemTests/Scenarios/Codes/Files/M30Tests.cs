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

/// <summary>M30, deleting a file</summary>
[TestFixture]
[GCode("M30")]
public class M30Tests : BenchFixture
{
    /// <summary>M30 deletes a file from the G-code directory</summary>
    /// <remarks>
    /// GCodes2.cpp case 30 deletes the named file relative to <c>Platform::GetGCodeDir()</c> and
    /// reports "delete failed" when it cannot. Deleting a file changes no object model field, so the
    /// file system and the M20 listing are what say it happened
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M30DeletesAFile()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd => sd.WriteGCode("doomed.gcode", "G4 P1\n"))
                                             .StartAsync();
        string path = Physical(bench, "gcodes", "doomed.gcode");
        Assert.That(File.Exists(path), Is.True, "the file exists before M30 runs");

        await bench.Host.ExecuteCodeAsync("M30 \"0:/gcodes/doomed.gcode\"");

        Assert.Multiple(async () =>
        {
            Assert.That(File.Exists(path), Is.False, "M30 deleted the file (GCodes2.cpp case 30)");
            Assert.That(await bench.Host.ExecuteCodeAsync("M20"), Does.Not.Contain("doomed.gcode"),
                        "M20 no longer lists the deleted file");
        });
        Assert.That(await bench.Host.ExecuteCodeAsync("M30 \"0:/gcodes/doomed.gcode\""),
                    Does.Contain("delete failed"),
                    "M30 reports a failure to delete (GCodes2.cpp case 30)");
    }
}
