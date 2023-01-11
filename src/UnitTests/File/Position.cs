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
            CodeFile file = new(filePath, DuetAPI.CodeChannel.File);
            Code code;

            // Line 1
            code = (await file.ReadCodeAsync())!;
            Assert.AreEqual(0, code.FilePosition);
            Assert.AreEqual(16, code.Length);

            // Line 2
            code = (await file.ReadCodeAsync())!;
            Assert.AreEqual(16, code.FilePosition);
            Assert.AreEqual(12, code.Length);

            // Line 3
            code = (await file.ReadCodeAsync())!;
            Assert.AreEqual(28, code.FilePosition);
            Assert.AreEqual(27, code.Length);

            // Go back to the first char of line 2. May be 15 if NL instead of CRNL is used
            file.Position = 16;

            // Read it again
            code = (await file.ReadCodeAsync())!;
            Assert.AreEqual(16, code.FilePosition);
            Assert.AreEqual(12, code.Length);
        }
    }
}
