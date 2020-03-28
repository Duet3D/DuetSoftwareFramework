using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using DuetAPI.Machine;
using NUnit.Framework;

namespace UnitTests.File
{
    [TestFixture]
    public class InfoParser
    {
        [Test]
        [TestCase("Cura.gcode")]
        [TestCase("Simplify3D.gcode")]
        [TestCase("Slic3r.gcode")]
        public async Task Test(string fileName)
        {
            string filePath = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "File/GCodes", fileName);
            ParsedFileInfo info = await DuetControlServer.Files.InfoParser.Parse(filePath);

            TestContext.Out.Write(JsonSerializer.Serialize(info, typeof(ParsedFileInfo), new JsonSerializerOptions { WriteIndented = true }));

            Assert.IsNotNull(info.FileName);
            Assert.AreNotEqual(0, info.Size);
            Assert.AreNotEqual(0, info.Height);
            Assert.AreNotEqual(0, info.FirstLayerHeight);
            Assert.AreNotEqual(0, info.LayerHeight);
            Assert.AreNotEqual(0, info.Filament.Count);
            Assert.IsNotEmpty(info.GeneratedBy);
            // Assert.AreNotEqual(0, info.PrintTime);
            // Assert.AreNotEqual(0, info.SimulatedTime);
        }

        [Test]
        public async Task TestEmpty()
        {
            string filePath = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "File/GCodes/Circle.gcode");
            ParsedFileInfo info = await DuetControlServer.Files.InfoParser.Parse(filePath);

            TestContext.Out.Write(JsonSerializer.Serialize(info, typeof(ParsedFileInfo), new JsonSerializerOptions { WriteIndented = true }));

            Assert.IsNotNull(info.FileName);
            Assert.AreNotEqual(0, info.Size);
            Assert.AreEqual(0.5, info.Height);
            Assert.AreEqual(0.5, info.FirstLayerHeight);
            Assert.AreEqual(0, info.LayerHeight);
            Assert.AreEqual(0, info.Filament.Count);
            Assert.IsNull(info.GeneratedBy);
            Assert.IsNull(info.PrintTime);
            Assert.IsNull(info.SimulatedTime);
        }
    }
}
