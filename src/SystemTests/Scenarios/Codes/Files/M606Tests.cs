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

/// <summary>M606, forking the job file</summary>
[TestFixture]
[GCode("M606")]
public class M606Tests : BenchFixture
{
    /// <summary>M606 forks the reader, and only from within a job file</summary>
    /// <remarks>
    /// GCodes5.cpp <c>ForkInputReader</c>: outside the File channel it answers
    /// "this command is valid only when running a job from a stored file" as a warning, and with no
    /// S parameter it reports which mode the reader is running in
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M606OutsideAJobFileIsRefused()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed);

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ExecuteCodeAsync("M606 S1"),
                        Does.Contain("this command is valid only when running a job from a stored file"),
                        "M606 S1 outside a job file is refused (GCodes5.cpp ForkInputReader)");
            Assert.That(await bench.Host.ExecuteCodeAsync("M606"),
                        Does.Contain("File reader is running in standard mode"),
                        "M606 with no S reports the reader mode (GCodes5.cpp ForkInputReader)");
        });
    }
}
