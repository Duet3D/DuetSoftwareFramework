#if false
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Utility;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace UnitTests.Commands
{
    [TestFixture]
    public class Code
    {
        [Test]
        public void ParseG28()
        {
            foreach (DuetAPI.Commands.Code code in Parse("G28 X Y"))
            {
                Assert.That(code.Type, Is.EqualTo(CodeType.GCode));
                Assert.That(code.MajorNumber, Is.EqualTo(28));
                Assert.That(code.MinorNumber, Is.Null);
                Assert.That(code.Parameters, Has.Count.EqualTo(2));
                Assert.That(code.Parameters[0].Letter, Is.EqualTo('X'));
                Assert.That(code.Parameters[0].IsNull, Is.True);
                Assert.That(code.Parameters[1].Letter, Is.EqualTo('Y'));
                Assert.That(code.Parameters[1].IsNull, Is.True);
            }
        }

        [Test]
        public void ParseG29()
        {
            foreach (DuetAPI.Commands.Code code in Parse("G29 S1 ; load heightmap"))
            {
                ClassicAssert.AreEqual(CodeType.GCode, code.Type);
                ClassicAssert.AreEqual(29, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(1, code.Parameters.Count);
                ClassicAssert.AreEqual('S', code.Parameters[0].Letter);
                ClassicAssert.AreEqual(1, code.GetInt('S', 0));
            }
        }

        [Test]
        public void ParseG53()
        {
            foreach (DuetAPI.Commands.Code code in Parse("G53"))
            {
                ClassicAssert.AreEqual(CodeType.GCode, code.Type);
                ClassicAssert.AreEqual(53, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
            }
        }

        [Test]
        public void ParseG54()
        {
            foreach (DuetAPI.Commands.Code code in Parse("G54.6"))
            {
                ClassicAssert.AreEqual(CodeType.GCode, code.Type);
                ClassicAssert.AreEqual(54, code.MajorNumber);
                ClassicAssert.AreEqual(6, code.MinorNumber);
            }
        }

        [Test]
        public void ParseG92()
        {
            foreach (DuetAPI.Commands.Code code in Parse("G92 X0 Y0 Z0"))
            {
                ClassicAssert.AreEqual(CodeType.GCode, code.Type);
                ClassicAssert.AreEqual(92, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);

                ClassicAssert.AreEqual(3, code.Parameters.Count);

                ClassicAssert.AreEqual('X', code.Parameters[0].Letter);
                ClassicAssert.AreEqual(0, (int)code.Parameters[0]);
                ClassicAssert.AreEqual('Y', code.Parameters[1].Letter);
                ClassicAssert.AreEqual(0, (int)code.Parameters[1]);
                ClassicAssert.AreEqual('Z', code.Parameters[2].Letter);
                ClassicAssert.AreEqual(0, (int)code.Parameters[2]);
            }
        }

        [Test]
        public void ParseM32()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M32 some fancy  file.g"))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(32, code.MajorNumber);
                ClassicAssert.AreEqual("some fancy  file.g", code.GetUnprecedentedString());
            }
        }

        [Test]
        public void ParseM92()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M92 E810:810:407:407"))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(92, code.MajorNumber);

                ClassicAssert.AreEqual(1, code.Parameters.Count);

                int[] steps = [810, 810, 407, 407];
                ClassicAssert.AreEqual(steps, code.GetIntArray('E')!);
            }
        }

        [Test]
        public void ParseM98()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M98 P\"config.g\""))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(98, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(1, code.Parameters.Count);
                ClassicAssert.AreEqual('P', code.Parameters[0].Letter);
                ClassicAssert.AreEqual("config.g", (string?)code.Parameters[0]);
            }
        }

        [Test]
        public void ParseM106()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M106 P1 C\"Fancy \"\" Fan\" H-1 S0.5"))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(106, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(4, code.Parameters.Count);
                ClassicAssert.AreEqual('P', code.Parameters[0].Letter);
                ClassicAssert.AreEqual(1, (int)code.Parameters[0]);
                ClassicAssert.AreEqual('C', code.Parameters[1].Letter);
                ClassicAssert.AreEqual("Fancy \" Fan", (string?)code.Parameters[1]);
                ClassicAssert.AreEqual('H', code.Parameters[2].Letter);
                ClassicAssert.AreEqual(-1, (int)code.Parameters[2]);
                ClassicAssert.AreEqual('S', code.Parameters[3].Letter);
                ClassicAssert.AreEqual(0.5, (float)code.Parameters[3], 0.0001);

                TestContext.Out.WriteLine(JsonSerializer.Serialize(code, typeof(DuetAPI.Commands.Code), new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        [Test]
        public void ParseEmptyM117()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M117 \"\""))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(117, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(1, code.Parameters.Count);
                ClassicAssert.AreEqual('@', code.Parameters[0].Letter);
                ClassicAssert.AreEqual(string.Empty, (string?)code.Parameters[0]);
            }
        }

        [Test]
        public void ParseM122DSF()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M122 \"DSF\""))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(122, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(1, code.Parameters.Count);
                ClassicAssert.AreEqual('@', code.Parameters[0].Letter);
                ClassicAssert.AreEqual("DSF", (string?)code.Parameters[0]);
            }
        }

#if false
        [Test]
        public void ParseM260()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M260 A0xF1 B0"))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(260, code.MajorNumber);
                ClassicAssert.AreEqual(2, code.Parameters.Count);
                ClassicAssert.AreEqual('A', code.Parameters[0].Letter);
                ClassicAssert.AreEqual(0xF1, (int)code.Parameters[0]);
                ClassicAssert.AreEqual('B', code.Parameters[1].Letter);
                ClassicAssert.AreEqual(0, (int)code.Parameters[1]);
            }

            foreach (DuetAPI.Commands.Code code in Parse("M260 A0XF1 B0"))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(260, code.MajorNumber);
                ClassicAssert.AreEqual(2, code.Parameters.Count);
                ClassicAssert.AreEqual('A', code.Parameters[0].Letter);
                ClassicAssert.AreEqual(0xF1, (int)code.Parameters[0]);
                ClassicAssert.AreEqual('B', code.Parameters[1].Letter);
                ClassicAssert.AreEqual(0, (int)code.Parameters[1]);
            }
        }
#endif

        [Test]
        public void TestBadM291()
        {
            using MemoryStream stream = new(Encoding.UTF8.GetBytes("M291 P\"Please select the tool to load.Press\"Cancel\" to abort\" R\"Load Tool\" S4 K{\"Cancel\",\"Tool#1\",\"Tool#2\",\"Tool#3\"};display message box with choices"));
            using StreamReader reader = new(stream);
            DuetAPI.Commands.Code result = new();
            Assert.Catch<CodeParserException>(() => DuetAPI.Commands.Code.Parse(reader, result));

            stream.Seek(0, SeekOrigin.Begin);
            CodeParserBuffer buffer = new(8192, false);
            Assert.CatchAsync<CodeParserException>(async () => await DuetAPI.Commands.Code.ParseAsync(stream, result, buffer));
        }

        [Test]
        public void ParseM302Compact()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M302D\"dummy\"P1"))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(302, code.MajorNumber);
                ClassicAssert.AreEqual(2, code.Parameters.Count);
                ClassicAssert.AreEqual('D', code.Parameters[0].Letter);
                ClassicAssert.AreEqual("dummy", (string?)code.Parameters[0]);
                ClassicAssert.AreEqual('P', code.Parameters[1].Letter);
                ClassicAssert.AreEqual(1, (int)code.Parameters[1]);
            }
        }

        [Test]
        public void ParseM563()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M563 P0 D0:1 H1:2                             ; Define tool 0"))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(563, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(3, code.Parameters.Count);
                ClassicAssert.AreEqual('P', code.Parameters[0].Letter);
                ClassicAssert.AreEqual(0, (int)code.Parameters[0]);
                ClassicAssert.AreEqual('D', code.Parameters[1].Letter);
                ClassicAssert.AreEqual(new int[] { 0, 1 }, (int[])code.Parameters[1]);
                ClassicAssert.AreEqual('H', code.Parameters[2].Letter);
                ClassicAssert.AreEqual(new int[] { 1, 2 }, (int[])code.Parameters[2]);
                ClassicAssert.AreEqual(" Define tool 0", code.Comment);
            }
        }

        [Test]
        public void ParseM569()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M569 P1.2 S1 T0.5"))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(569, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(CodeFlags.IsLastCode, code.Flags);
                ClassicAssert.AreEqual(3, code.Parameters.Count);
                ClassicAssert.AreEqual('P', code.Parameters[0].Letter);
                ClassicAssert.AreEqual(new DriverId(1, 2), (DriverId)code.Parameters[0]);
                ClassicAssert.AreEqual('S', code.Parameters[1].Letter);
                ClassicAssert.AreEqual(1, (int)code.Parameters[1]);
                ClassicAssert.AreEqual('T', code.Parameters[2].Letter);
                ClassicAssert.AreEqual(0.5, (float)code.Parameters[2], 0.0001);
            }
        }

        [Test]
        public void ParseM569Array()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M569 P1.2:3.4 S1 T0.5"))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(569, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(CodeFlags.IsLastCode, code.Flags);
                ClassicAssert.AreEqual(3, code.Parameters.Count);
                ClassicAssert.AreEqual('P', code.Parameters[0].Letter);
                ClassicAssert.AreEqual(new DriverId[] { new(1, 2), new(3, 4) }, (DriverId[])code.Parameters[0]);
                ClassicAssert.AreEqual('S', code.Parameters[1].Letter);
                ClassicAssert.AreEqual(1, (int)code.Parameters[1]);
                ClassicAssert.AreEqual('T', code.Parameters[2].Letter);
                ClassicAssert.AreEqual(0.5, (float)code.Parameters[2], 0.0001);
            }
        }

        [Test]
        public void ParseM574()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M574 Y2 S1 P\"io1.in\";comment"))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(574, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(CodeFlags.IsLastCode, code.Flags);
                ClassicAssert.AreEqual(3, code.Parameters.Count);
                ClassicAssert.AreEqual('Y', code.Parameters[0].Letter);
                ClassicAssert.AreEqual(2, (int)code.Parameters[0]);
                ClassicAssert.AreEqual('S', code.Parameters[1].Letter);
                ClassicAssert.AreEqual(1, (int)code.Parameters[1]);
                ClassicAssert.AreEqual('P', code.Parameters[2].Letter);
                ClassicAssert.AreEqual("io1.in", (string?)code.Parameters[2]);
                ClassicAssert.AreEqual("comment", code.Comment);
            }
        }

        [Test]
        public void ParseM587()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M587 S\"TestAp\" P\"Some pass\" I192.168.1.123 J192.168.1.254 K255.255.255.0"))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(587, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(CodeFlags.IsLastCode, code.Flags);
                ClassicAssert.AreEqual(5, code.Parameters.Count);
                ClassicAssert.AreEqual('S', code.Parameters[0].Letter);
                ClassicAssert.AreEqual("TestAp", (string?)code.Parameters[0]);
                ClassicAssert.AreEqual('P', code.Parameters[1].Letter);
                ClassicAssert.AreEqual("Some pass", (string?)code.Parameters[1]);
                ClassicAssert.AreEqual('I', code.Parameters[2].Letter);
                ClassicAssert.AreEqual(IPAddress.Parse("192.168.1.123"), (IPAddress?)code.Parameters[2]);
                ClassicAssert.AreEqual('J', code.Parameters[3].Letter);
                ClassicAssert.AreEqual(IPAddress.Parse("192.168.1.254"), (IPAddress?)code.Parameters[3]);
                ClassicAssert.AreEqual('K', code.Parameters[4].Letter);
                ClassicAssert.AreEqual(IPAddress.Parse("255.255.255.0"), (IPAddress?)code.Parameters[4]);
            }
        }

        [Test]
        public void ParseM915()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M915 P2:0.3:1.4 S22"))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(915, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(CodeFlags.IsLastCode, code.Flags);
                ClassicAssert.AreEqual(2, code.Parameters.Count);
                ClassicAssert.AreEqual('P', code.Parameters[0].Letter);
                DriverId[] driverIds = [new(2), new(3), new(1, 4)];
                ClassicAssert.AreEqual(driverIds, (DriverId[])code.Parameters[0]);
                ClassicAssert.AreEqual('S', code.Parameters[1].Letter);
                ClassicAssert.AreEqual(22, (int)code.Parameters[1]);
            }
        }

        [Test]
        public void ParseT3()
        {
            foreach (DuetAPI.Commands.Code code in Parse("T3 P4 S\"foo\""))
            {
                ClassicAssert.AreEqual(CodeType.TCode, code.Type);
                ClassicAssert.AreEqual(3, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(CodeFlags.IsLastCode, code.Flags);
                ClassicAssert.AreEqual(2, code.Parameters.Count);
                ClassicAssert.AreEqual('P', code.Parameters[0].Letter);
                ClassicAssert.AreEqual(4, (int)code.Parameters[0]);
                ClassicAssert.AreEqual('S', code.Parameters[1].Letter);
                ClassicAssert.AreEqual("foo", (string?)code.Parameters[1]);
                ClassicAssert.AreEqual("T3 P4 S\"foo\"", code.ToString());
            }
        }

        [Test]
        public void ParseQuotedM32()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M32 \"foo bar.g\""))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(32, code.MajorNumber);
                ClassicAssert.AreEqual("foo bar.g", code.GetUnprecedentedString());
            }
        }


        [Test]
        public void ParseChar()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M1234 P'{' S1"))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(1234, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(CodeFlags.IsLastCode, code.Flags);
                ClassicAssert.AreEqual(2, code.Parameters.Count);
                ClassicAssert.AreEqual('P', code.Parameters[0].Letter);
                ClassicAssert.AreEqual("{", (string)code.Parameters[0]);
                ClassicAssert.AreEqual('S', code.Parameters[1].Letter);
                ClassicAssert.AreEqual(1, (int)code.Parameters[1]);
            }
        }

        [Test]
        public void ParseApostropheM32()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M32 \"C ''t H, , . ., ''T H.gcode\""))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(32, code.MajorNumber);
                ClassicAssert.AreEqual("C 't H, , . ., 'T H.gcode", code.GetUnprecedentedString());
            }
        }

        [Test]
        public void ParseUnquotedM32()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M32 foo bar.g"))
            {
                ClassicAssert.AreEqual(0, code.Indent);
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(32, code.MajorNumber);
                ClassicAssert.AreEqual("foo bar.g", code.GetUnprecedentedString());
            }
        }

        [Test]
        public void ParseM584WithExpressions()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M584 E123:{456} 'f7.8 'g9.0"))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(584, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(3, code.Parameters.Count);
                ClassicAssert.AreEqual('E', code.Parameters[0].Letter);
                ClassicAssert.IsTrue(code.Parameters[0].IsExpression);
                ClassicAssert.AreEqual("{123:{456}}", (string?)code.Parameters[0]);
                ClassicAssert.AreEqual('f', code.Parameters[1].Letter);
                ClassicAssert.AreEqual(new DriverId(7, 8), (DriverId)code.Parameters[1]);
                ClassicAssert.AreEqual('g', code.Parameters[2].Letter);
                ClassicAssert.AreEqual(new DriverId(9, 0), (DriverId)code.Parameters[2]);
            }

            foreach (DuetAPI.Commands.Code code in Parse("M584 E{123}:{456}:789"))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(584, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(1, code.Parameters.Count);
                ClassicAssert.AreEqual('E', code.Parameters[0].Letter);
                ClassicAssert.IsTrue(code.Parameters[0].IsExpression);
                ClassicAssert.AreEqual("{{123}:{456}:789}", (string?)code.Parameters[0]);
            }

            foreach (DuetAPI.Commands.Code code in Parse("M584 E{123}:{456}:{789}"))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(584, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(1, code.Parameters.Count);
                ClassicAssert.AreEqual('E', code.Parameters[0].Letter);
                ClassicAssert.IsTrue(code.Parameters[0].IsExpression);
                ClassicAssert.AreEqual("{123}:{456}:{789}", (string?)code.Parameters[0]);
            }

            foreach (DuetAPI.Commands.Code code in Parse("M92 E{123,456}"))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(92, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(1, code.Parameters.Count);
                ClassicAssert.AreEqual('E', code.Parameters[0].Letter);
                ClassicAssert.IsTrue(code.Parameters[0].IsExpression);
                ClassicAssert.AreEqual("{123,456}", (string?)code.Parameters[0]);
            }

        }

        [Test]
        public void ParseM586WithComment()
        {
            foreach (DuetAPI.Commands.Code code in Parse(" \t M586 P2 S0                               ; Disable Telnet"))
            {
                ClassicAssert.AreEqual(5, code.Indent);
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(586, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(2, code.Parameters.Count);
                ClassicAssert.AreEqual('P', code.Parameters[0].Letter);
                ClassicAssert.AreEqual(2, (int)code.Parameters[0]);
                ClassicAssert.AreEqual('S', code.Parameters[1].Letter);
                ClassicAssert.AreEqual(0, (int)code.Parameters[1]);
                ClassicAssert.AreEqual(" Disable Telnet", code.Comment);
            }
        }

        [Test]
        public void ParseG1Absolute()
        {
            foreach (DuetAPI.Commands.Code code in Parse("G53 G1 X3 Y1.25 A2 'a3 b4"))
            {
                ClassicAssert.AreEqual(CodeFlags.EnforceAbsolutePosition | CodeFlags.IsLastCode, code.Flags);
                ClassicAssert.AreEqual(1, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(5, code.Parameters.Count);
                ClassicAssert.AreEqual('X', code.Parameters[0].Letter);
                ClassicAssert.AreEqual(3, (int)code.Parameters[0]);
                ClassicAssert.AreEqual('Y', code.Parameters[1].Letter);
                ClassicAssert.AreEqual(1.25, (float)code.Parameters[1], 0.0001);
                ClassicAssert.AreEqual('A', code.Parameters[2].Letter);
                ClassicAssert.AreEqual(2, (float)code.Parameters[2], 0.0001);
                ClassicAssert.AreEqual('a', code.Parameters[3].Letter);
                ClassicAssert.AreEqual(3, (float)code.Parameters[3], 0.0001);
                ClassicAssert.AreEqual('B', code.Parameters[4].Letter);
                ClassicAssert.AreEqual(4, (float)code.Parameters[4], 0.0001);
            }
        }

        [Test]
        public void ParseG1Expression()
        {
            foreach (DuetAPI.Commands.Code code in Parse("G1 X{machine.axes[0].maximum - 10} Y{machine.axes[1].maximum - 10}"))
            {
                ClassicAssert.AreEqual(CodeType.GCode, code.Type);
                ClassicAssert.AreEqual(1, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(2, code.Parameters.Count);
                ClassicAssert.IsTrue(code.Parameters[0].IsExpression);
                ClassicAssert.AreEqual('X', code.Parameters[0].Letter);
                ClassicAssert.AreEqual("{machine.axes[0].maximum - 10}", (string?)code.Parameters[0]);
                ClassicAssert.IsTrue(code.Parameters[1].IsExpression);
                ClassicAssert.AreEqual('Y', code.Parameters[1].Letter);
                ClassicAssert.AreEqual("{machine.axes[1].maximum - 10}", (string?)code.Parameters[1]);
            }
        }

        [Test]
        public void ParseM32Expression()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M32 {my.test.value}"))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(32, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(1, code.Parameters.Count);
                ClassicAssert.AreEqual('@', code.Parameters[0].Letter);
                ClassicAssert.AreEqual(true, code.Parameters[0].IsExpression);
                ClassicAssert.AreEqual("{my.test.value}", (string?)code.Parameters[0]);
            }
        }

        [Test]
        public void ParseM117()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M117 Hello world!;comment"))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(117, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(1, code.Parameters.Count);
                ClassicAssert.AreEqual('@', code.Parameters[0].Letter);
                ClassicAssert.IsFalse(code.Parameters[0].IsExpression);
                ClassicAssert.AreEqual("Hello world!", (string?)code.Parameters[0]);
                ClassicAssert.AreEqual("comment", code.Comment);
            }
        }

        [Test]
        public void ParseM118Unicode()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M118 P\"💡 - LEDs on\""))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(118, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(1, code.Parameters.Count);
                ClassicAssert.AreEqual('P', code.Parameters[0].Letter);
                ClassicAssert.IsFalse(code.Parameters[0].IsExpression);
                ClassicAssert.AreEqual("💡 - LEDs on", (string?)code.Parameters[0]);
            }
        }

        [Test]
        public void ParseM117Expression()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M117 { \"Axis \" ^ ( move.axes[0].letter ) ^ \" not homed. Please wait while all axes are homed\" }"))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(117, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(1, code.Parameters.Count);
                ClassicAssert.AreEqual('@', code.Parameters[0].Letter);
                ClassicAssert.IsTrue(code.Parameters[0].IsExpression);
                ClassicAssert.AreEqual("{ \"Axis \" ^ ( move.axes[0].letter ) ^ \" not homed. Please wait while all axes are homed\" }", (string?)code.Parameters[0]);
            }
        }

        [Test]
        public void ParseEmptyComments()
        {
            foreach (DuetAPI.Commands.Code code in Parse(";"))
            {
                ClassicAssert.AreEqual(CodeType.Comment, code.Type);
                ClassicAssert.AreEqual(string.Empty, code.Comment);
            }

            foreach (DuetAPI.Commands.Code code in Parse("()"))
            {
                ClassicAssert.AreEqual(CodeType.Comment, code.Type);
                ClassicAssert.AreEqual(string.Empty, code.Comment);
            }
        }

        [Test]
        public void ParseLineNumber()
        {
            foreach (DuetAPI.Commands.Code code in Parse("  N123 G1 X5 Y3"))
            {
                ClassicAssert.AreEqual(2, code.Indent);
                ClassicAssert.AreEqual(123, code.LineNumber);
                ClassicAssert.AreEqual(CodeType.GCode, code.Type);
                ClassicAssert.AreEqual(1, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(2, code.Parameters.Count);
                ClassicAssert.AreEqual('X', code.Parameters[0].Letter);
                ClassicAssert.AreEqual(5, (int)code.Parameters[0]);
                ClassicAssert.AreEqual('Y', code.Parameters[1].Letter);
                ClassicAssert.AreEqual(3, (int)code.Parameters[1]);
            }
        }

        [Test]
        public void ParseIf()
        {
            foreach (DuetAPI.Commands.Code code in Parse("if machine.tool.is.great <= {(0.03 - 0.001) + {foo}} ;some nice comment"))
            {
                ClassicAssert.AreEqual(0, code.Indent);
                ClassicAssert.AreEqual(CodeType.Keyword, code.Type);
                ClassicAssert.AreEqual(KeywordType.If, code.Keyword);
                ClassicAssert.AreEqual("machine.tool.is.great <= {(0.03 - 0.001) + {foo}}", code.KeywordArgument);
                ClassicAssert.AreEqual("some nice comment", code.Comment);
            }
        }

        [Test]
        public void ParseIf2()
        {
            foreach (DuetAPI.Commands.Code code in Parse("  if {abs(move.calibration.final.deviation - move.calibration.initial.deviation)} < 0.005"))
            {
                ClassicAssert.AreEqual(2, code.Indent);
                ClassicAssert.AreEqual(CodeType.Keyword, code.Type);
                ClassicAssert.AreEqual(KeywordType.If, code.Keyword);
                ClassicAssert.AreEqual("{abs(move.calibration.final.deviation - move.calibration.initial.deviation)} < 0.005", code.KeywordArgument);
                ClassicAssert.IsNull(code.Comment);
            }
        }

        [Test]
        public void ParseElif()
        {
            foreach (DuetAPI.Commands.Code code in Parse("  elif true"))
            {
                ClassicAssert.AreEqual(2, code.Indent);
                ClassicAssert.AreEqual(CodeType.Keyword, code.Type);
                ClassicAssert.AreEqual(KeywordType.ElseIf, code.Keyword);
                ClassicAssert.AreEqual("true", code.KeywordArgument);
            }
        }

        [Test]
        public void ParseElse()
        {
            foreach (DuetAPI.Commands.Code code in Parse("  else"))
            {
                ClassicAssert.AreEqual(2, code.Indent);
                ClassicAssert.AreEqual(CodeType.Keyword, code.Type);
                ClassicAssert.AreEqual(KeywordType.Else, code.Keyword);
                ClassicAssert.IsNull(code.KeywordArgument);
            }
        }

        [Test]
        public void ParseWhile()
        {
            foreach (DuetAPI.Commands.Code code in Parse("  while machine.autocal.stddev > 0.04"))
            {
                ClassicAssert.AreEqual(2, code.Indent);
                ClassicAssert.AreEqual(CodeType.Keyword, code.Type);
                ClassicAssert.AreEqual(KeywordType.While, code.Keyword);
                ClassicAssert.AreEqual("machine.autocal.stddev > 0.04", code.KeywordArgument);
            }

            foreach (DuetAPI.Commands.Code code in Parse("  while var.i < var.N"))
            {
                Assert.That(code.Indent, Is.EqualTo(2));
                Assert.That(code.Type, Is.EqualTo(CodeType.Keyword));
                Assert.That(code.Keyword, Is.EqualTo(KeywordType.While));
                Assert.That(code.KeywordArgument, Is.EqualTo("var.i < var.N"));
            }
        }

        [Test]
        public void ParseBreak()
        {
            foreach (DuetAPI.Commands.Code code in Parse("    break"))
            {
                ClassicAssert.AreEqual(4, code.Indent);
                ClassicAssert.AreEqual(CodeType.Keyword, code.Type);
                ClassicAssert.AreEqual(KeywordType.Break, code.Keyword);
                ClassicAssert.IsNull(code.KeywordArgument);
            }
        }

        [Test]
        public void ParseContinue()
        {
            foreach (DuetAPI.Commands.Code code in Parse("  continue"))
            {
                ClassicAssert.AreEqual(2, code.Indent);
                ClassicAssert.AreEqual(CodeType.Keyword, code.Type);
                ClassicAssert.AreEqual(KeywordType.Continue, code.Keyword);
                ClassicAssert.IsNull(code.KeywordArgument);
            }
        }

        [Test]
        public void ParseAbort()
        {
            foreach (DuetAPI.Commands.Code code in Parse("    abort foo bar"))
            {
                ClassicAssert.AreEqual(4, code.Indent);
                ClassicAssert.AreEqual(CodeType.Keyword, code.Type);
                ClassicAssert.AreEqual(KeywordType.Abort, code.Keyword);
                ClassicAssert.AreEqual("foo bar", code.KeywordArgument);
            }
        }

        [Test]
        public void ParseVar()
        {
            foreach (DuetAPI.Commands.Code code in Parse("  var asdf=0.34"))
            {
                ClassicAssert.AreEqual(2, code.Indent);
                ClassicAssert.AreEqual(CodeType.Keyword, code.Type);
                ClassicAssert.AreEqual(KeywordType.Var, code.Keyword);
                ClassicAssert.AreEqual("asdf=0.34", code.KeywordArgument);
            }
        }

        [Test]
        public void ParseSet()
        {
            foreach (DuetAPI.Commands.Code code in Parse("  set asdf=\"meh\""))
            {
                ClassicAssert.AreEqual(2, code.Indent);
                ClassicAssert.AreEqual(CodeType.Keyword, code.Type);
                ClassicAssert.AreEqual(KeywordType.Set, code.Keyword);
                ClassicAssert.AreEqual("asdf=\"meh\"", code.KeywordArgument);
                ClassicAssert.AreEqual(0, code.Parameters.Count);
            }
        }

        [Test]
        public void ParseGlobal()
        {
            foreach (DuetAPI.Commands.Code code in Parse(" \tglobal foo=\"bar\""))
            {
                ClassicAssert.AreEqual(4, code.Indent);
                ClassicAssert.AreEqual(CodeType.Keyword, code.Type);
                ClassicAssert.AreEqual(KeywordType.Global, code.Keyword);
                ClassicAssert.AreEqual("foo=\"bar\"", code.KeywordArgument);
            }
        }

        [Test]
        public void ParseEcho()
        {
            foreach (DuetAPI.Commands.Code code in Parse("echo {{3 + 3} + (volumes[0].freeSpace - 4)}"))
            {
                ClassicAssert.AreEqual(0, code.Indent);
                ClassicAssert.AreEqual(CodeType.Keyword, code.Type);
                ClassicAssert.AreEqual(KeywordType.Echo, code.Keyword);
                ClassicAssert.AreEqual("{{3 + 3} + (volumes[0].freeSpace - 4)}", code.KeywordArgument);
            }
        }

        [Test]
        public void ParseEchoWithSemicolon()
        {
            foreach (DuetAPI.Commands.Code code in Parse("echo \"; this should work\""))
            {
                ClassicAssert.AreEqual(0, code.Indent);
                ClassicAssert.AreEqual(CodeType.Keyword, code.Type);
                ClassicAssert.AreEqual(KeywordType.Echo, code.Keyword);
                ClassicAssert.AreEqual("\"; this should work\"", code.KeywordArgument);
            }
        }

        [Test]
        public void ParseEchoWithBraces()
        {
            foreach (DuetAPI.Commands.Code code in Parse(" \techo \"debug \" ^ abs(3)"))
            {
                ClassicAssert.AreEqual(4, code.Indent);
                ClassicAssert.AreEqual(CodeType.Keyword, code.Type);
                ClassicAssert.AreEqual(KeywordType.Echo, code.Keyword);
                ClassicAssert.AreEqual("\"debug \" ^ abs(3)", code.KeywordArgument);
            }
        }

        [Test]
        public async Task ParseEchoWithQuote()
        {
            DuetControlServer.Commands.SimpleCode simpleCode = new() { Code = "echo \"M98 P\"\"revo/define-tool.g\"\" S\"" };
            List<DuetControlServer.Commands.Code> codes = [];
            await foreach (DuetControlServer.Commands.Code code in simpleCode.ParseAsync())
            {
                codes.Add(code);
            }

            ClassicAssert.AreEqual(1, codes.Count);

            ClassicAssert.AreEqual(CodeType.Keyword, codes[0].Type);
            ClassicAssert.AreEqual("\"M98 P\"\"revo/define-tool.g\"\" S\"", codes[0].KeywordArgument);
        }

        [Test]
        public void ParseEchoWithUnicode()
        {
            foreach (DuetAPI.Commands.Code code in Parse("echo \"💡 - LEDs on\""))
            {
                ClassicAssert.AreEqual(CodeType.Keyword, code.Type);
                ClassicAssert.AreEqual(KeywordType.Echo, code.Keyword);
                ClassicAssert.AreEqual("\"💡 - LEDs on\"", code.KeywordArgument);
            }
        }

        [Test]
        public void ParseDynamicT()
        {
            foreach (DuetAPI.Commands.Code code in Parse("T{my.expression} P0"))
            {
                ClassicAssert.AreEqual(CodeFlags.IsLastCode, code.Flags);
                ClassicAssert.AreEqual(CodeType.TCode, code.Type);
                ClassicAssert.IsNull(code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(2, code.Parameters.Count);
                ClassicAssert.AreEqual('T', code.Parameters[0].Letter);
                ClassicAssert.AreEqual("{my.expression}", (string?)code.Parameters[0]);
                ClassicAssert.AreEqual('P', code.Parameters[1].Letter);
                ClassicAssert.AreEqual(0, (int)code.Parameters[1]);
            }
        }

        [Test]
        public void ParseNoSpaceComment()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M84 XYE; disable motors"))
            {
                ClassicAssert.AreEqual(CodeFlags.IsLastCode, code.Flags);
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(84, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(3, code.Parameters.Count);
                ClassicAssert.AreEqual('X', code.Parameters[0].Letter);
                ClassicAssert.IsTrue(code.Parameters[0].IsNull);
                ClassicAssert.AreEqual('Y', code.Parameters[1].Letter);
                ClassicAssert.IsTrue(code.Parameters[1].IsNull);
                ClassicAssert.AreEqual('E', code.Parameters[2].Letter);
                ClassicAssert.IsTrue(code.Parameters[2].IsNull);
                ClassicAssert.AreEqual(" disable motors", code.Comment);
            }
        }

        [Test]
        public void ParseSpecialNumbers()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M106 P0x123 S3"))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(106, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(2, code.Parameters.Count);
                ClassicAssert.AreEqual('P', code.Parameters[0].Letter);
                ClassicAssert.AreEqual('S', code.Parameters[1].Letter);
                ClassicAssert.AreEqual(0x123, (int)code.Parameters[0]);
                ClassicAssert.AreEqual(3, (int)code.Parameters[1]);
                ClassicAssert.IsNull(code.Comment);
            }

            foreach (DuetAPI.Commands.Code code in Parse("M106 P0 S3e2 ; foo"))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(106, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(2, code.Parameters.Count);
                ClassicAssert.AreEqual('P', code.Parameters[0].Letter);
                ClassicAssert.AreEqual('S', code.Parameters[1].Letter);
                ClassicAssert.AreEqual(0, (int)code.Parameters[0]);
                ClassicAssert.AreEqual(3e2, (float)code.Parameters[1]);
                ClassicAssert.AreEqual(" foo", code.Comment);
            }

            foreach (DuetAPI.Commands.Code code in Parse("M106 P0 S3e-2 ; foobar"))
            {
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(106, code.MajorNumber);
                ClassicAssert.IsNull(code.MinorNumber);
                ClassicAssert.AreEqual(2, code.Parameters.Count);
                ClassicAssert.AreEqual('P', code.Parameters[0].Letter);
                ClassicAssert.AreEqual('S', code.Parameters[1].Letter);
                ClassicAssert.AreEqual(0, (int)code.Parameters[0]);
                ClassicAssert.AreEqual(3e-2, (float)code.Parameters[1], 1e-3);
                ClassicAssert.AreEqual(" foobar", code.Comment);
            }
        }

        [Test]
        public async Task SimpleCodes()
        {
            DuetControlServer.Commands.SimpleCode simpleCode = new() { Code = "G91 G1 X5 Y2" };
            List<DuetControlServer.Commands.Code> codes = [];
            await foreach (DuetControlServer.Commands.Code code in simpleCode.ParseAsync())
            {
                codes.Add(code);
            }

            ClassicAssert.AreEqual(2, codes.Count);

            ClassicAssert.AreEqual(CodeType.GCode, codes[0].Type);
            ClassicAssert.AreEqual(91, codes[0].MajorNumber);

            ClassicAssert.AreEqual(CodeType.GCode, codes[1].Type);
            ClassicAssert.AreEqual(1, codes[1].MajorNumber);
            ClassicAssert.AreEqual(2, codes[1].Parameters.Count);
            ClassicAssert.AreEqual('X', codes[1].Parameters[0].Letter);
            ClassicAssert.AreEqual(5, (int)codes[1].Parameters[0]);
            ClassicAssert.AreEqual('Y', codes[1].Parameters[1].Letter);
            ClassicAssert.AreEqual(2, (int)codes[1].Parameters[1]);
        }

        [Test]
        public async Task SimpleCodesG53Line()
        {
            DuetControlServer.Commands.SimpleCode simpleCode = new() { Code = "G53 G1 X100 G0 Y200\nG1 Z50" };
            List<DuetControlServer.Commands.Code> codes = [];
            await foreach (DuetControlServer.Commands.Code code in simpleCode.ParseAsync())
            {
                codes.Add(code);
            }
            ClassicAssert.AreEqual(3, codes.Count);

            ClassicAssert.AreEqual(1, codes[0].MajorNumber);
            ClassicAssert.AreEqual(1, codes[0].Parameters.Count);
            ClassicAssert.AreEqual('X', codes[0].Parameters[0].Letter);
            ClassicAssert.AreEqual(100, (int)codes[0].Parameters[0]);
            ClassicAssert.IsTrue(codes[0].Flags.HasFlag(CodeFlags.EnforceAbsolutePosition));

            ClassicAssert.AreEqual(0, codes[1].MajorNumber);
            ClassicAssert.AreEqual(1, codes[1].Parameters.Count);
            ClassicAssert.AreEqual('Y', codes[1].Parameters[0].Letter);
            ClassicAssert.AreEqual(200, (int)codes[1].Parameters[0]);
            ClassicAssert.IsTrue(codes[1].Flags.HasFlag(CodeFlags.EnforceAbsolutePosition));

            ClassicAssert.AreEqual(1, codes[2].MajorNumber);
            ClassicAssert.AreEqual(1, codes[2].Parameters.Count);
            ClassicAssert.AreEqual('Z', codes[2].Parameters[0].Letter);
            ClassicAssert.AreEqual(50, (int)codes[2].Parameters[0]);
            ClassicAssert.IsFalse(codes[2].Flags.HasFlag(CodeFlags.EnforceAbsolutePosition));
        }

        [Test]
        public async Task SimpleCodesNL()
        {
            DuetControlServer.Commands.SimpleCode simpleCode = new() { Code = "G91\nG1 X5 Y2" };
            List<DuetControlServer.Commands.Code> codes = [];
            await foreach (DuetControlServer.Commands.Code code in simpleCode.ParseAsync())
            {
                codes.Add(code);
            }

            ClassicAssert.AreEqual(2, codes.Count);

            ClassicAssert.AreEqual(CodeType.GCode, codes[0].Type);
            ClassicAssert.AreEqual(91, codes[0].MajorNumber);

            ClassicAssert.AreEqual(CodeType.GCode, codes[1].Type);
            ClassicAssert.AreEqual(1, codes[1].MajorNumber);
            ClassicAssert.AreEqual(2, codes[1].Parameters.Count);
            ClassicAssert.AreEqual('X', codes[1].Parameters[0].Letter);
            ClassicAssert.AreEqual(5, (int)codes[1].Parameters[0]);
            ClassicAssert.AreEqual('Y', codes[1].Parameters[1].Letter);
            ClassicAssert.AreEqual(2, (int)codes[1].Parameters[1]);
        }

        [Test]
        public async Task SimpleCodesIndented()
        {
            DuetControlServer.Commands.SimpleCode simpleCode = new() { Code = "    G1 X5 Y5 G1 X10 Y10\nG1 X15 Y15" };
            List<DuetControlServer.Commands.Code> codes = [];
            await foreach (DuetControlServer.Commands.Code code in simpleCode.ParseAsync())
            {
                codes.Add(code);
            }

            ClassicAssert.AreEqual(3, codes.Count);

            ClassicAssert.AreEqual(CodeType.GCode, codes[0].Type);
            ClassicAssert.AreEqual(CodeFlags.None, codes[0].Flags);
            ClassicAssert.AreEqual(4, codes[0].Indent);
            ClassicAssert.AreEqual(1, codes[0].MajorNumber);
            ClassicAssert.AreEqual(2, codes[0].Parameters.Count);
            ClassicAssert.AreEqual('X', codes[0].Parameters[0].Letter);
            ClassicAssert.AreEqual(5, (int)codes[0].Parameters[0]);
            ClassicAssert.AreEqual('Y', codes[0].Parameters[1].Letter);
            ClassicAssert.AreEqual(5, (int)codes[0].Parameters[1]);

            ClassicAssert.AreEqual(CodeType.GCode, codes[1].Type);
            ClassicAssert.AreEqual(CodeFlags.IsLastCode, codes[1].Flags);
            ClassicAssert.AreEqual(4, codes[1].Indent);
            ClassicAssert.AreEqual(1, codes[1].MajorNumber);
            ClassicAssert.AreEqual(2, codes[1].Parameters.Count);
            ClassicAssert.AreEqual('X', codes[1].Parameters[0].Letter);
            ClassicAssert.AreEqual(10, (int)codes[1].Parameters[0]);
            ClassicAssert.AreEqual('Y', codes[1].Parameters[1].Letter);
            ClassicAssert.AreEqual(10, (int)codes[1].Parameters[1]);

            ClassicAssert.AreEqual(CodeType.GCode, codes[2].Type);
            ClassicAssert.AreEqual(CodeFlags.IsLastCode, codes[2].Flags);
            ClassicAssert.AreEqual(0, codes[2].Indent);
            ClassicAssert.AreEqual(1, codes[2].MajorNumber);
            ClassicAssert.AreEqual(2, codes[2].Parameters.Count);
            ClassicAssert.AreEqual('X', codes[2].Parameters[0].Letter);
            ClassicAssert.AreEqual(15, (int)codes[2].Parameters[0]);
            ClassicAssert.AreEqual('Y', codes[2].Parameters[1].Letter);
            ClassicAssert.AreEqual(15, (int)codes[2].Parameters[1]);
        }

        [Test]
        public async Task ParseAsync()
        {
            string codeString = "G53 G1 X0 Y5 F3000 G0 X5 Y10";
            byte[] codeBytes = Encoding.UTF8.GetBytes(codeString);
            await using (MemoryStream memoryStream = new(codeBytes))
            {
                CodeParserBuffer buffer = new(128, true);
                DuetAPI.Commands.Code code = new() { LineNumber = 1 };

                await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer);
                ClassicAssert.AreEqual(CodeType.GCode, code.Type);
                ClassicAssert.AreEqual(1, code.MajorNumber);
                ClassicAssert.AreEqual(CodeFlags.EnforceAbsolutePosition, code.Flags);
                ClassicAssert.AreEqual(1, code.LineNumber);
                ClassicAssert.AreEqual(3, code.Parameters.Count);
                ClassicAssert.AreEqual(0, code.GetInt('X'));
                ClassicAssert.AreEqual(5, code.GetInt('Y'));
                ClassicAssert.AreEqual(3000, code.GetInt('F'));


                code.Reset();
                await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer);
                ClassicAssert.AreEqual(CodeType.GCode, code.Type);
                ClassicAssert.AreEqual(0, code.MajorNumber);
                ClassicAssert.AreEqual(CodeFlags.EnforceAbsolutePosition | CodeFlags.IsLastCode, code.Flags);
                ClassicAssert.AreEqual(1, code.LineNumber);
                ClassicAssert.AreEqual(2, code.Parameters.Count);
                ClassicAssert.AreEqual(5, code.GetInt('X'));
                ClassicAssert.AreEqual(10, code.GetInt('Y'));
            }

            codeString = "G1 X1 Y5 F3000\nG1 X5 F300\nG0 Y40";
            codeBytes = Encoding.UTF8.GetBytes(codeString);
            await using (MemoryStream memoryStream = new(codeBytes))
            {
                CodeParserBuffer buffer = new(128, true);

                DuetAPI.Commands.Code code = new() { LineNumber = 0 };
                await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer);

                code.Reset();
                await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer);

                code.Reset();
                await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer);

                ClassicAssert.AreEqual(CodeType.GCode, code.Type);
                ClassicAssert.AreEqual(0, code.MajorNumber);
                ClassicAssert.AreEqual(3, code.LineNumber);
            }


            codeString = "G1 X1 Y5 F3000\nX5 F300\nY40";
            codeBytes = Encoding.UTF8.GetBytes(codeString);
            await using (MemoryStream memoryStream = new(codeBytes))
            {
                CodeParserBuffer buffer = new(128, true) { MayRepeatCode = true };
                DuetAPI.Commands.Code code = new() { LineNumber = 0 };

                await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer);
                ClassicAssert.AreEqual(1, code.LineNumber);
                ClassicAssert.AreEqual(CodeType.GCode, code.Type);
                ClassicAssert.AreEqual(1, code.MajorNumber);
                ClassicAssert.AreEqual(3, code.Parameters.Count);
                ClassicAssert.AreEqual(1, code.GetInt('X'));
                ClassicAssert.AreEqual(5, code.GetInt('Y'));
                ClassicAssert.AreEqual(3000, code.GetInt('F'));

                code.Reset();
                await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer);
                ClassicAssert.AreEqual(CodeType.GCode, code.Type);
                ClassicAssert.AreEqual(1, code.MajorNumber);
                ClassicAssert.AreEqual(2, code.LineNumber);
                ClassicAssert.AreEqual(2, code.Parameters.Count);
                ClassicAssert.AreEqual(5, code.GetInt('X'));
                ClassicAssert.AreEqual(300, code.GetInt('F'));

                code.Reset();
                await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer);
                ClassicAssert.AreEqual(3, code.LineNumber);
                ClassicAssert.AreEqual(CodeType.GCode, code.Type);
                ClassicAssert.AreEqual(1, code.MajorNumber);
                ClassicAssert.AreEqual(1, code.Parameters.Count);
                ClassicAssert.AreEqual(40, code.GetInt('Y'));
            }

            codeString = "G1 X1 Y5 F3000\n  G53 G1 X5 F300\n    G53 G0 Y40 G1 Z50\n  G4 S3\nG1 Z3";
            codeBytes = Encoding.UTF8.GetBytes(codeString);
            await using (MemoryStream memoryStream = new(codeBytes))
            {
                CodeParserBuffer buffer = new(128, true);

                DuetAPI.Commands.Code code = new() { LineNumber = 0 };
                await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer);
                ClassicAssert.AreEqual(CodeFlags.IsLastCode, code.Flags);
                ClassicAssert.AreEqual(0, code.Indent);
                ClassicAssert.AreEqual(1, code.LineNumber);

                code.Reset();
                await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer);
                ClassicAssert.AreEqual(CodeFlags.EnforceAbsolutePosition | CodeFlags.IsLastCode, code.Flags);
                ClassicAssert.AreEqual(2, code.Indent);
                ClassicAssert.AreEqual(2, code.LineNumber);

                code.Reset();
                await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer);
                ClassicAssert.AreEqual(CodeFlags.EnforceAbsolutePosition, code.Flags);
                ClassicAssert.AreEqual(4, code.Indent);
                ClassicAssert.AreEqual(3, code.LineNumber);

                code.Reset();
                await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer);
                ClassicAssert.AreEqual(CodeFlags.EnforceAbsolutePosition | CodeFlags.IsLastCode, code.Flags);
                ClassicAssert.AreEqual(4, code.Indent);
                ClassicAssert.AreEqual(3, code.LineNumber);

                code.Reset();
                await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer);
                ClassicAssert.AreEqual(CodeFlags.IsLastCode, code.Flags);
                ClassicAssert.AreEqual(2, code.Indent);
                ClassicAssert.AreEqual(4, code.LineNumber);

                code.Reset();
                await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer);
                ClassicAssert.AreEqual(CodeFlags.IsLastCode, code.Flags);
                ClassicAssert.AreEqual(0, code.Indent);
                ClassicAssert.AreEqual(5, code.LineNumber);
            }

            codeString = "M291 P\"Please go to <a href=\"\"https://www.duet3d.com/StartHere\"\" target=\"\"_blank\"\">this</a> page for further instructions on how to set it up.\" R\"Welcome to your new Duet 3!\" S1 T0";
            codeBytes = Encoding.UTF8.GetBytes(codeString);
            await using (MemoryStream memoryStream = new(codeBytes))
            {
                CodeParserBuffer buffer = new(128, true);

                DuetAPI.Commands.Code code = new();
                await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer);
                ClassicAssert.AreEqual(CodeType.MCode, code.Type);
                ClassicAssert.AreEqual(291, code.MajorNumber);
                ClassicAssert.AreEqual("Please go to <a href=\"https://www.duet3d.com/StartHere\" target=\"_blank\">this</a> page for further instructions on how to set it up.", code.GetString('P'));
                ClassicAssert.AreEqual("Welcome to your new Duet 3!", code.GetString('R'));
                ClassicAssert.AreEqual(1, code.GetInt('S'));
                ClassicAssert.AreEqual(0, code.GetInt('T'));
            }
        }

        public static IEnumerable<DuetAPI.Commands.Code> Parse(string code)
        {
            yield return new DuetAPI.Commands.Code(code);

            byte[] codeBytes = Encoding.UTF8.GetBytes(code);
            using MemoryStream memoryStream = new(codeBytes);
            CodeParserBuffer buffer = new(128, true);
            DuetAPI.Commands.Code codeObj = new();
            DuetAPI.Commands.Code.ParseAsync(memoryStream, codeObj, buffer).AsTask().Wait();
            yield return codeObj;
        }
    }
}
#endif
