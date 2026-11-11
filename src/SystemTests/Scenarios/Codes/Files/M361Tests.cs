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

/// <summary>M361, a file's embedded thumbnail</summary>
[TestFixture]
[GCode("M361")]
public class M361Tests : BenchFixture
{
    /// <summary>A file carrying one PNG thumbnail of known dimensions and data</summary>
    private const string ThumbnailFile = """
        ; thumbnail begin 32x32 16
        ; QUJDREVGR0hJSktM
        ; thumbnail end
        G1 Z1 F300
        """;

    /// <summary>The base64 payload of <see cref="ThumbnailFile"/>'s thumbnail</summary>
    private const string ThumbnailData = "QUJDREVGR0hJSktM";

    /// <summary>
    /// M36.1 returns the base64 data of an embedded thumbnail at the offset the object model gives
    /// for it
    /// </summary>
    /// <remarks>
    /// FileInfoParser.cpp <c>ProcessThumbnail</c> stores width, height, encoded size, format and the
    /// file offset of each thumbnail, reported through the PrintMonitor.cpp thumbnails table.
    /// GCodes2.cpp case 36 fraction 1 reads the file from that offset through
    /// <c>RepRap::GetFileFragment</c> (RepRap.cpp), which strips the comment markers and answers
    /// with the data and the next offset
    /// </remarks>
    [Test]
    public async Task M361ReturnsTheEmbeddedThumbnail()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd => sd.WriteGCode("thumb.gcode", ThumbnailFile))
                                             .StartAsync();
        await bench.Host.ExecuteCodeAsync("M23 \"0:/gcodes/thumb.gcode\"");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Job.File.Thumbnails[0].Width), Is.EqualTo(32),
                        "job.file.thumbnails[0].width comes from the thumbnail header (FileInfoParser.cpp ProcessThumbnail)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Job.File.Thumbnails[0].Height), Is.EqualTo(32),
                        "job.file.thumbnails[0].height comes from the thumbnail header (FileInfoParser.cpp ProcessThumbnail)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Job.File.Thumbnails[0].Size), Is.EqualTo(ThumbnailData.Length),
                        "job.file.thumbnails[0].size is the encoded length the header declares (FileInfoParser.cpp ProcessThumbnail)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Job.File.Thumbnails[0].Format), Is.EqualTo(ThumbnailInfoFormat.PNG),
                        "a thumbnail begin block is PNG (FileInfoParser.cpp ProcessThumbnail, param 0)");
        });

        long offset = (long)await bench.Host.ReadModelAsync(model => model.Job.File.Thumbnails[0].Offset);
        JsonElement thumbnail = ParseJson("M36.1", await bench.Host.ExecuteCodeAsync($"M36.1 P\"0:/gcodes/thumb.gcode\" S{offset}"))
                                    .GetProperty("thumbnail");
        Assert.Multiple(() =>
        {
            Assert.That(thumbnail.TryGetProperty("data", out JsonElement data) ? data.GetString() ?? string.Empty : string.Empty,
                        Does.Contain(ThumbnailData),
                        "M36.1 returns the base64 data found at job.file.thumbnails[0].offset (RepRap.cpp GetFileFragment)");
            Assert.That(thumbnail.GetProperty("next").GetInt64(), Is.Zero,
                        "M36.1 answers next 0 once the thumbnail has ended (RepRap.cpp GetFileFragment)");
        });
    }
}
