using DuetAPI.Commands;
using DuetControlServer.Files;
using NUnit.Framework;
using System.IO;
using System.Threading.Tasks;

namespace UnitTests.File
{
    [TestFixture]
    public class Position
    {
        [Test]
        public async Task TestPosition()
        {
            string filePath = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "../../../File/GCodes/Cura.gcode");
            CodeFile file = new CodeFile(filePath, DuetAPI.CodeChannel.File);
            Code code;

            // Line 1
            code = await file.ReadCodeAsync();
            Assert.AreEqual(0, code.FilePosition);
            Assert.AreEqual(15, code.Length);

            // Line 2
            code = await file.ReadCodeAsync();
            Assert.AreEqual(15, code.FilePosition);
            Assert.AreEqual(11, code.Length);

            // Line 3
            code = await file.ReadCodeAsync();
            Assert.AreEqual(26, code.FilePosition);
            Assert.AreEqual(26, code.Length);

            // Go back to line 2
            file.Position = 15;

            // Read it again
            code = await file.ReadCodeAsync();
            Assert.AreEqual(15, code.FilePosition);
            Assert.AreEqual(11, code.Length);
        }
    }
}
