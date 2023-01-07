using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;

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

            Assert.IsNotNull(info.FileName);
            Assert.AreNotEqual(0, info.Size);
            Assert.AreNotEqual(0, info.Height);
            Assert.AreNotEqual(0, info.LayerHeight);
            Assert.AreNotEqual(0, info.NumLayers);
            Assert.AreNotEqual(0, info.Filament.Count);
            Assert.IsNotEmpty(info.GeneratedBy);
            // Assert.AreNotEqual(0, info.PrintTime);
            // Assert.AreNotEqual(0, info.SimulatedTime);
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
            Assert.AreEqual(info.Thumbnails.Count, thumbnailCount);
        }

        [TestCase("Thumbnail.gcode")]
        public async Task TestThumbnailResponse(string fileName)
        {
            string filePath = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "../../../File/GCodes", fileName);
            GCodeFileInfo info = await DuetControlServer.Files.InfoParser.Parse(filePath, true);

            string thumbnailResponse = await DuetControlServer.Files.InfoParser.ParseThumbnail(filePath, info.Thumbnails[0].Offset);
            Assert.IsTrue(thumbnailResponse.Contains(info.Thumbnails[0].Data![..1024]));

            TestContext.Out.Write(thumbnailResponse);
        }

        [Test]
        public async Task TestEmpty()
        {
            string filePath = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "../../../File/GCodes/Circle.gcode");
            GCodeFileInfo info = await DuetControlServer.Files.InfoParser.Parse(filePath, true);

            TestContext.Out.Write(JsonSerializer.Serialize(info, typeof(GCodeFileInfo), new JsonSerializerOptions { WriteIndented = true }));

            Assert.IsNotNull(info.FileName);
            Assert.AreNotEqual(0, info.Size);
            Assert.AreEqual(0.5, info.Height);
            Assert.AreEqual(0, info.LayerHeight);
            Assert.AreEqual(0, info.Filament.Count);
            Assert.IsNull(info.GeneratedBy);
            Assert.IsNull(info.PrintTime);
            Assert.IsNull(info.SimulatedTime);
        }
    }
}
