﻿using DuetAPI.Commands;
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

            Code code;
            do
            {
                code = await macro.ReadCodeAsync();
                if (code == null)
                {
                    break;
                }

                Console.WriteLine(code);
            }
            while (true);

            // End
        }
    }
}
