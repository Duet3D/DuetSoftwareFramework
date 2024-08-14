#if false
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace UnitTests.File
{
    [TestFixture]
    public class InfoParser
    {
        [Test]
        [TestCase("Cura.gcode")]
        [TestCase("PrusaSlicer.gcode")]
        [TestCase("Simplify3D.gcode")]
        [TestCase("Slic3r.gcode")]
        public async Task Test(string fileName)
        {
            string filePath = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "../../../File/GCodes", fileName);
            GCodeFileInfo info = await DuetControlServer.Files.InfoParser.Parse(filePath, true);

            TestContext.Out.Write(JsonSerializer.Serialize(info, typeof(GCodeFileInfo), new JsonSerializerOptions { WriteIndented = true }));

            ClassicAssert.IsNotNull(info.FileName);
            ClassicAssert.AreNotEqual(0, info.Size);
            ClassicAssert.AreNotEqual(0, info.Height);
            ClassicAssert.AreNotEqual(0, info.LayerHeight);
            ClassicAssert.AreNotEqual(0, info.NumLayers);
            ClassicAssert.AreNotEqual(0, info.Filament.Count);
            ClassicAssert.IsNotEmpty(info.GeneratedBy);
            // ClassicAssert.AreNotEqual(0, info.PrintTime);
            // ClassicAssert.AreNotEqual(0, info.SimulatedTime);
        }

        [TestCase("Thumbnail.gcode", 2)]
        [TestCase("Thumbnail_JPG.gcode", 1)]
        [TestCase("Thumbnail_QOI.gcode", 2)]
        [TestCase("BenchyIcon.gcode", 1)]
        public async Task TestThumbnails(string fileName, int thumbnailCount)
        {
            string filePath = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "../../../File/GCodes", fileName);
            GCodeFileInfo info = await DuetControlServer.Files.InfoParser.Parse(filePath, true);
            TestContext.Out.Write(JsonSerializer.Serialize(info, typeof(GCodeFileInfo), new JsonSerializerOptions { WriteIndented = true }));
            ClassicAssert.AreEqual(info.Thumbnails.Count, thumbnailCount);
        }

        [TestCase("Thumbnail.gcode")]
        public async Task TestThumbnailResponse(string fileName)
        {
            string filePath = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "../../../File/GCodes", fileName);
            GCodeFileInfo info = await DuetControlServer.Files.InfoParser.Parse(filePath, true);

            string thumbnailResponse = await DuetControlServer.Files.InfoParser.ParseThumbnail(filePath, info.Thumbnails[0].Offset);
            ClassicAssert.IsTrue(thumbnailResponse.Contains(info.Thumbnails[0].Data![..1024]));

            TestContext.Out.Write(thumbnailResponse);
        }

        [Test]
        public async Task TestEmpty()
        {
            string filePath = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "../../../File/GCodes/Circle.gcode");
            GCodeFileInfo info = await DuetControlServer.Files.InfoParser.Parse(filePath, true);

            TestContext.Out.Write(JsonSerializer.Serialize(info, typeof(GCodeFileInfo), new JsonSerializerOptions { WriteIndented = true }));

            ClassicAssert.IsNotNull(info.FileName);
            ClassicAssert.AreNotEqual(0, info.Size);
            ClassicAssert.AreEqual(0.5, info.Height);
            ClassicAssert.AreEqual(0, info.LayerHeight);
            ClassicAssert.AreEqual(0, info.Filament.Count);
            ClassicAssert.IsNull(info.GeneratedBy);
            ClassicAssert.IsNull(info.PrintTime);
            ClassicAssert.IsNull(info.SimulatedTime);
        }
    }
}
#endif
