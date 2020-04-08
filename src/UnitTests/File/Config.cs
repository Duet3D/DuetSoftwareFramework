using DuetAPI.Commands;
using DuetControlServer.Files;
using NUnit.Framework;
using System;
using System.IO;

namespace UnitTests.File
{
    [TestFixture]
    public class Config
    {
        [Test]
        public void ProcessConfig()
        {
            string filePath = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "../../../File/GCodes/config.g");
            BaseFile macro = new BaseFile(filePath, DuetAPI.CodeChannel.Trigger);

            do
            {
                Code code = macro.ReadCode();
                Console.WriteLine(code);
            } while (!macro.IsFinished);

            // End
        }
    }
}
