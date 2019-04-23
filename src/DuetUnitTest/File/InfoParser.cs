using System.IO;
using System.Threading.Tasks;
using DuetAPI;
using DuetControlServer;
using Newtonsoft.Json;
using NUnit.Framework;

namespace DuetUnitTest.File
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
            ParsedFileInfo info = await FileInfoParser.Parse(filePath);

            TestContext.Out.Write(JsonConvert.SerializeObject(info, Formatting.Indented));

            Assert.IsNotNull(info.FileName);
            Assert.AreNotEqual(0, info.Size);
            Assert.IsNotNull(info.LastModified);
            Assert.AreNotEqual(0, info.Height);
            Assert.AreNotEqual(0, info.FirstLayerHeight);
            Assert.AreNotEqual(0, info.LayerHeight);
            Assert.AreNotEqual(0, info.Filament.Length);
            Assert.AreNotEqual("", info.GeneratedBy);
            // Assert.AreNotEqual(0, info.PrintTime);
            // Assert.AreNotEqual(0, info.SimulatedTime);
        }

        [Test]
        public async Task TestEmpty()
        {
            string filePath = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "File/GCodes/Circle.gcode");
            ParsedFileInfo info = await FileInfoParser.Parse(filePath);

            TestContext.Out.Write(JsonConvert.SerializeObject(info, Formatting.Indented));

            Assert.IsNotNull(info.FileName);
            Assert.AreNotEqual(0, info.Size);
            Assert.IsNotNull(info.LastModified);
            Assert.AreEqual(0, info.Height);
            Assert.AreEqual(0.5, info.FirstLayerHeight);
            Assert.AreEqual(0, info.LayerHeight);
            Assert.AreEqual(0, info.Filament.Length);
            Assert.AreEqual("", info.GeneratedBy);
            Assert.AreEqual(0, info.PrintTime);
            Assert.AreEqual(0, info.SimulatedTime);
        }
    }
}
