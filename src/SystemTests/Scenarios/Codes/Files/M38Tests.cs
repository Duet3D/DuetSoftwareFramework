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

/// <summary>M38, a file's checksum</summary>
[TestFixture]
[GCode("M38")]
public class M38Tests : BenchFixture
{
    /// <summary>M38 reports the CRC32 checksum of a file</summary>
    /// <remarks>
    /// GCodes2.cpp case 38 hashes the file through GCodes3.cpp <c>StartHash</c> and
    /// <c>AdvanceHash</c>, which answer with the CRC32 as eight lower-case hexadecimal digits, and
    /// "Cannot open file: &lt;name&gt;" for a file that is not there
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M38ReportsTheFileChecksum()
    {
        const string content = "M117 known content\n";
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd => sd.WriteGCode("hashed.gcode", content))
                                             .StartAsync();

        Assert.Multiple(async () =>
        {
            Assert.That((await bench.Host.ExecuteCodeAsync("M38 \"0:/gcodes/hashed.gcode\"")).Trim(),
                        Is.EqualTo("091d63b8"),
                        "M38 reports the CRC32 of the file as eight hex digits (GCodes3.cpp AdvanceHash)");
            Assert.That(await bench.Host.ExecuteCodeAsync("M38 \"0:/gcodes/absent.gcode\""),
                        Does.Contain("Cannot open file"),
                        "M38 reports a file it cannot open (GCodes2.cpp case 38)");
        });
    }
}
