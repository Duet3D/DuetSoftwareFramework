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

namespace SystemTests.Scenarios.Codes.Files;

/// <summary>M20, listing files</summary>
[TestFixture]
[GCode("M20")]
public class M20Tests : BenchFixture
{
    /// <summary>
    /// M20 lists the names the SD card holds, from the G-code directory by default and from the
    /// directory P names otherwise
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp case 20: the directory defaults to <c>Platform::GetGCodeDir()</c>, and with no S
    /// parameter the names are listed after a "GCode files:" header, quoted and comma separated,
    /// for the RepRapFirmware compatibility the bench runs in
    /// </remarks>
    [Test]
    public async Task M20ListsTheFilesOnTheSdCard()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd =>
        {
            sd.WriteGCode("first.gcode", "G4 P1\n");
            sd.WriteGCode("second.gcode", "G4 P1\n");
        }).StartAsync();

        string gcodes = await bench.Host.ExecuteCodeAsync("M20");
        string sys = await bench.Host.ExecuteCodeAsync("M20 P\"0:/sys\"");

        Assert.Multiple(() =>
        {
            Assert.That(gcodes, Does.Contain("GCode files:"),
                        "M20 heads the list with GCode files: (GCodes2.cpp case 20)");
            Assert.That(gcodes, Does.Contain("\"first.gcode\""),
                        "M20 lists first.gcode, which the virtual SD card holds");
            Assert.That(gcodes, Does.Contain("\"second.gcode\""),
                        "M20 lists second.gcode, which the virtual SD card holds");
            Assert.That(gcodes, Does.Not.Contain("config.g"),
                        "M20 lists the G-code directory, not the system directory");
            Assert.That(sys, Does.Contain("\"config.g\""),
                        "M20 P lists the named directory (GCodes2.cpp case 20, the P parameter)");
            Assert.That(sys, Does.Contain("\"pause.g\""),
                        "M20 P\"0:/sys\" lists every file the system directory holds");
        });
    }
}
