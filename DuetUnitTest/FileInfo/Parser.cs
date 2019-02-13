using DuetAPI.Commands;
using Newtonsoft.Json;
using NUnit.Framework;
using System.IO;
using System.Threading.Tasks;

namespace DuetUnitTest.FileInfo
{
    [TestFixture]
    public class Parser
    {
        [Test]
        [TestCase("Cura.gcode")]
        [TestCase("Simplify3D.gcode")]
        [TestCase("Slic3r.gcode")]
        public async Task Test(string fileName)
        {
            string filePath = Path.Combine(Directory.GetCurrentDirectory(), "FileInfo", fileName);
            FileInfoResult info = await DuetControlServer.File.GetFileInfo(filePath);

            TestContext.Out.Write(JsonConvert.SerializeObject(info, Formatting.Indented));

            Assert.IsNotNull(info.FileName);
            Assert.AreNotEqual(0, info.Size);
            Assert.IsNotNull(info.LastModified);
            Assert.AreNotEqual(0, info.Height);
            Assert.AreNotEqual(0, info.FirstLayerHeight);
            Assert.AreNotEqual(0, info.LayerHeight);
            Assert.AreNotEqual(0, info.Filament.Length);
            Assert.IsNotNull(info.GeneratedBy);
            // Assert.AreNotEqual(0, info.PrintTime);
            // Assert.AreNotEqual(0, info.SimulatedTime);
        }
    }
}
