using DuetAPI.Commands;
using DuetControlServer.FileExecution;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DuetUnitTest.File
{
    [TestFixture]
    public class Position
    {
        [Test]
        public void TestPosition()
        {
            string filePath = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "File/GCodes/Cura.gcode");
            BaseFile file = new BaseFile(filePath, DuetAPI.CodeChannel.File);
            Code code;

            // Line 1
            code = file.ReadCode().Result;
            Assert.AreEqual(0, code.FilePosition);

            // Line 2
            code = file.ReadCode().Result;
            Assert.AreEqual(15, code.FilePosition);

            // Line 3
            code = file.ReadCode().Result;
            Assert.AreEqual(26, code.FilePosition);
        }
    }
}
