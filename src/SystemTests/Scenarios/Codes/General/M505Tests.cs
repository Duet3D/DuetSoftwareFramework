using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.General;

/// <summary>M505 and M505.1, the system and web directories</summary>
[TestFixture]
[GCode("M505", "M505.1")]
public class M505Tests : BenchFixture
{
    /// <summary>
    /// M505 reports and sets the system directory, which the object model carries as
    /// directories.system.
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 505: without P it replies "Sys file path is %s"; with P it calls
    /// Platform::SetSysDir. The directory is reported as directories.system (RepRap.cpp
    /// objectModelTable, default "0:/sys/")
    /// </remarks>
    [Test]
    public async Task M505SetsTheSystemDirectory()
    {
        await using Bench bench = await Bench.StartAsync();
        string omSys = await bench.Host.ReadModelAsync(model => model.Directories.System);
        Assert.That(omSys, Is.EqualTo("0:/sys/"), "directories.system defaults to 0:/sys/ (RRF RepRap.cpp directories table)");
        Assert.That((await bench.Host.ExecuteCodeAsync("M505")).Trim(), Is.EqualTo($"Sys file path is {omSys}"),
                    "M505 without P reports directories.system (RRF GCodes2.cpp case 505)");

        await bench.Host.ExecuteCodeAsync("M470 P\"0:/sys2\"");
        string reply = await bench.Host.ExecuteCodeAsync("M505 P\"0:/sys2\"");
        Assert.That(reply, Does.Not.Contain("Error"), "M505 P accepts an existing directory (RRF Platform::SetSysDir)");

        omSys = await bench.Host.ReadModelAsync(model => model.Directories.System);
        Assert.That(omSys, Does.StartWith("0:/sys2"), "M505 P sets directories.system (RRF Platform::SetSysDir)");
        Assert.That((await bench.Host.ExecuteCodeAsync("M505")).Trim(), Is.EqualTo($"Sys file path is {omSys}"),
                    "M505's report follows directories.system after the change (RRF GCodes2.cpp case 505)");
    }

    /// <summary>
    /// M505.1 reports and sets the web directory, which the object model carries as
    /// directories.web.
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 505 with command fraction 1: "HTTP file path is %s" and
    /// Platform::SetWebDir; directories.web defaults to "0:/www/" (RepRap.cpp)
    /// </remarks>
    [Test]
    public async Task M505Dot1SetsTheWebDirectory()
    {
        await using Bench bench = await Bench.StartAsync();
        string omWeb = await bench.Host.ReadModelAsync(model => model.Directories.Web);
        Assert.That(omWeb, Is.EqualTo("0:/www/"), "directories.web defaults to 0:/www/ (RRF RepRap.cpp directories table)");
        Assert.That((await bench.Host.ExecuteCodeAsync("M505.1")).Trim(), Is.EqualTo($"HTTP file path is {omWeb}"),
                    "M505.1 without P reports directories.web (RRF GCodes2.cpp case 505)");

        await bench.Host.ExecuteCodeAsync("M470 P\"0:/www2\"");
        string reply = await bench.Host.ExecuteCodeAsync("M505.1 P\"0:/www2\"");
        Assert.That(reply, Does.Not.Contain("Error"), "M505.1 P accepts an existing directory (RRF Platform::SetWebDir)");

        omWeb = await bench.Host.ReadModelAsync(model => model.Directories.Web);
        Assert.That(omWeb, Does.StartWith("0:/www2"), "M505.1 P sets directories.web (RRF Platform::SetWebDir)");
        Assert.That((await bench.Host.ExecuteCodeAsync("M505.1")).Trim(), Is.EqualTo($"HTTP file path is {omWeb}"),
                    "M505.1's report follows directories.web after the change (RRF GCodes2.cpp case 505)");
    }
}
