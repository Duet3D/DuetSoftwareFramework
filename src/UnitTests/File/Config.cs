using DuetAPI.Commands;
using DuetControlServer.Files;
using NUnit.Framework;
using System;
using System.IO;
using System.Threading.Tasks;

namespace UnitTests.File
{
    [TestFixture]
    public class Config
    {
        [Test]
        public async Task ProcessConfig()
        {
            string filePath = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "../../../File/GCodes/config.g");
            CodeFile macro = new CodeFile(filePath, DuetAPI.CodeChannel.Trigger);

            do
            {
                Code code = await macro.ReadCodeAsync();
                Console.WriteLine(code);
            } while (!macro.IsClosed);

            // End
        }
    }
}
