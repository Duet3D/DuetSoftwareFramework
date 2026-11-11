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

/// <summary>M39, the volumes</summary>
[TestFixture]
[GCode("M39")]
public class M39Tests : BenchFixture
{
    /// <summary>
    /// M39 reports the same numbers the object model holds
    /// </summary>
    /// <remarks>
    /// MassStorage.cpp OBJECT_MODEL table: <c>mounted</c> is unconditional, while <c>capacity</c>,
    /// <c>freeSpace</c> and <c>partitionSize</c> are reported only while the volume is mounted.
    /// GCodes2.cpp case 21 mounts the card and case 39 reports the same values, with S2 giving them
    /// as the SDinfo JSON object
    /// </remarks>
    [Test]
    public async Task M39ReportsVolume0()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed);

        string report = await bench.Host.ExecuteCodeAsync("M39");
        JsonElement info = ParseJson("M39 S2", await bench.Host.ExecuteCodeAsync("M39 S2")).GetProperty("SDinfo");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Volumes[0].Mounted), Is.True,
                        "volumes[0].mounted true (MassStorage.cpp OBJECT_MODEL mounted)");
            Assert.That(await NumberAsync(bench.Host, model => model.Volumes[0].Capacity), Is.GreaterThan(0.0),
                        "a mounted volume reports volumes[0].capacity in bytes (MassStorage.cpp OBJECT_MODEL capacity)");
            Assert.That(await NumberAsync(bench.Host, model => model.Volumes[0].FreeSpace), Is.GreaterThan(0.0),
                        "a mounted volume reports volumes[0].freeSpace in bytes (MassStorage.cpp OBJECT_MODEL freeSpace)");
            Assert.That(report, Does.Contain("SD card in slot 0: capacity"),
                        "M39 reports the slot and its capacity (GCodes2.cpp case 39)");
            Assert.That(info.TryGetProperty("present", out JsonElement present) ? present.GetInt32() : 0, Is.EqualTo(1),
                        "M39 S2 reports the mounted card as present (GCodes2.cpp case 39, format 2)");
            Assert.That(info.TryGetProperty("capacity", out JsonElement jsonCapacity) ? jsonCapacity.GetInt64() : 0L,
                        Is.EqualTo((long)await NumberAsync(bench.Host, model => model.Volumes[0].Capacity)),
                        "M39 S2 reports the same capacity as volumes[0].capacity");
        });
    }
}
