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

/// <summary>M470, M471 and M472, creating, renaming and deleting files and directories</summary>
[TestFixture]
[GCode("M470", "M471", "M472")]
public class M470M471M472Tests : BenchFixture
{
    /// <summary>
    /// M470 creates a directory, M471 renames into it, and M472 deletes it with its contents
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp cases 470, 471 and 472: P names the directory to create or delete, S and T the
    /// old and new names for a rename, D1 allows a rename to overwrite an existing target and R1
    /// makes a delete recursive. None of them writes an object model field, so the file system is
    /// what the assertions read
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M470M471AndM472ManageTheFileSystem()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd => sd.WriteGCode("moving.gcode", "G4 P1\n"))
                                             .StartAsync();

        await bench.Host.ExecuteCodeAsync("M470 P\"0:/gcodes/archive\"");
        Assert.That(Directory.Exists(Path.Combine(bench.Host.Sd.Root, "gcodes", "archive")), Is.True,
                    "M470 created the directory (GCodes2.cpp case 470)");

        await bench.Host.ExecuteCodeAsync("M471 S\"0:/gcodes/moving.gcode\" T\"0:/gcodes/archive/moved.gcode\"");
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(Physical(bench, "gcodes", "moving.gcode")), Is.False,
                        "M471 moved the file away from its old name (GCodes2.cpp case 471)");
            Assert.That(File.Exists(Path.Combine(bench.Host.Sd.Root, "gcodes", "archive", "moved.gcode")), Is.True,
                        "M471 moved the file to its new name (GCodes2.cpp case 471)");
        });

        Assert.That(await bench.Host.ExecuteCodeAsync("M472 P\"0:/gcodes/archive\""),
                    Does.Contain("delete failed"),
                    "M472 without R1 refuses to delete a directory that is not empty (GCodes2.cpp case 472)");

        await bench.Host.ExecuteCodeAsync("M472 P\"0:/gcodes/archive\" R1");
        Assert.That(Directory.Exists(Path.Combine(bench.Host.Sd.Root, "gcodes", "archive")), Is.False,
                    "M472 R1 deleted the directory and its contents (GCodes2.cpp case 472)");
    }
}
