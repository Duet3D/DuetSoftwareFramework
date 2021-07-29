using DuetAPI.Commands;
using DuetAPI.Utility;
using NUnit.Framework;
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
                Assert.AreEqual(CodeType.GCode, code.Type);
                Assert.AreEqual(28, code.MajorNumber);
                Assert.IsNull(code.MinorNumber);
                Assert.AreEqual(2, code.Parameters.Count);
                Assert.AreEqual('X', code.Parameters[0].Letter);
                Assert.AreEqual('Y', code.Parameters[1].Letter);
            }
        }

        [Test]
        public void ParseG29()
        {
            foreach (DuetAPI.Commands.Code code in Parse("G29 S1 ; load heightmap"))
            {
                Assert.AreEqual(CodeType.GCode, code.Type);
                Assert.AreEqual(29, code.MajorNumber);
                Assert.IsNull(code.MinorNumber);
                Assert.AreEqual(1, code.Parameters.Count);
                Assert.AreEqual('S', code.Parameters[0].Letter);
                Assert.AreEqual(1, (int)code.Parameter('S', 0));
            }
        }

        [Test]
        public void ParseG53()
        {
            foreach (DuetAPI.Commands.Code code in Parse("G53"))
            {
                Assert.AreEqual(CodeType.GCode, code.Type);
                Assert.AreEqual(53, code.MajorNumber);
                Assert.IsNull(code.MinorNumber);
            }
        }

        [Test]
        public void ParseG54()
        {
            foreach (DuetAPI.Commands.Code code in Parse("G54.6"))
            {
                Assert.AreEqual(CodeType.GCode, code.Type);
                Assert.AreEqual(54, code.MajorNumber);
                Assert.AreEqual(6, code.MinorNumber);
            }
        }

        [Test]
        public void ParseG92()
        {
            foreach (DuetAPI.Commands.Code code in Parse("G92 XYZ"))
            {
                Assert.AreEqual(CodeType.GCode, code.Type);
                Assert.AreEqual(92, code.MajorNumber);
                Assert.IsNull(code.MinorNumber);

                Assert.AreEqual(3, code.Parameters.Count);

                Assert.AreEqual('X', code.Parameters[0].Letter);
                Assert.AreEqual(0, (int)code.Parameters[0]);
                Assert.AreEqual('Y', code.Parameters[1].Letter);
                Assert.AreEqual(0, (int)code.Parameters[1]);
                Assert.AreEqual('Z', code.Parameters[2].Letter);
                Assert.AreEqual(0, (int)code.Parameters[2]);
            }
        }

        [Test]
        public void ParseM32()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M32 some fancy  file.g"))
            {
                Assert.AreEqual(CodeType.MCode, code.Type);
                Assert.AreEqual(32, code.MajorNumber);
                Assert.AreEqual("some fancy  file.g", code.GetUnprecedentedString());
            }
        }

        [Test]
        public void ParseM92()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M92 E810:810:407:407"))
            {
                Assert.AreEqual(CodeType.MCode, code.Type);
                Assert.AreEqual(92, code.MajorNumber);

                Assert.AreEqual(1, code.Parameters.Count);

                int[] steps = { 810, 810, 407, 407 };
                Assert.AreEqual(steps, (int[])code.Parameter('E'));
            }
        }

        [Test]
        public void ParseM98()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M98 P\"config.g\""))
            {
                Assert.AreEqual(CodeType.MCode, code.Type);
                Assert.AreEqual(98, code.MajorNumber);
                Assert.IsNull(code.MinorNumber);
                Assert.AreEqual(1, code.Parameters.Count);
                Assert.AreEqual('P', code.Parameters[0].Letter);
                Assert.AreEqual("config.g", (string)code.Parameters[0]);
            }
        }

        [Test]
        public void ParseM106()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M106 P1 C\"Fancy \"\" Fan\" H-1 S0.5"))
            {
                Assert.AreEqual(CodeType.MCode, code.Type);
                Assert.AreEqual(106, code.MajorNumber);
                Assert.IsNull(code.MinorNumber);
                Assert.AreEqual(4, code.Parameters.Count);
                Assert.AreEqual('P', code.Parameters[0].Letter);
                Assert.AreEqual(1, (int)code.Parameters[0]);
                Assert.AreEqual('C', code.Parameters[1].Letter);
                Assert.AreEqual("Fancy \" Fan", (string)code.Parameters[1]);
                Assert.AreEqual('H', code.Parameters[2].Letter);
                Assert.AreEqual(-1, (int)code.Parameters[2]);
                Assert.AreEqual('S', code.Parameters[3].Letter);
                Assert.AreEqual(0.5, code.Parameters[3], 0.0001);

                TestContext.Out.WriteLine(JsonSerializer.Serialize(code, typeof(DuetAPI.Commands.Code), new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        [Test]
        public void ParseEmptyM117()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M117 \"\""))
            {
                Assert.AreEqual(CodeType.MCode, code.Type);
                Assert.AreEqual(117, code.MajorNumber);
                Assert.IsNull(code.MinorNumber);
                Assert.AreEqual(1, code.Parameters.Count);
                Assert.AreEqual('@', code.Parameters[0].Letter);
                Assert.AreEqual(string.Empty, (string)code.Parameters[0]);
            }
        }

        [Test]
        public void ParseM122DSF()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M122 \"DSF\""))
            {
                Assert.AreEqual(CodeType.MCode, code.Type);
                Assert.AreEqual(122, code.MajorNumber);
                Assert.IsNull(code.MinorNumber);
                Assert.AreEqual(1, code.Parameters.Count);
                Assert.AreEqual('@', code.Parameters[0].Letter);
                Assert.AreEqual("DSF", (string)code.Parameters[0]);
            }
        }

        [Test]
        public void ParseM302Compact()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M302D\"dummy\"P1"))
            {
                Assert.AreEqual(CodeType.MCode, code.Type);
                Assert.AreEqual(302, code.MajorNumber);
                Assert.AreEqual(2, code.Parameters.Count);
                Assert.AreEqual('D', code.Parameters[0].Letter);
                Assert.AreEqual("dummy", (string)code.Parameters[0]);
                Assert.AreEqual('P', code.Parameters[1].Letter);
                Assert.AreEqual(1, (int)code.Parameters[1]);
            }
        }

        [Test]
        public void ParseM563()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M563 P0 D0:1 H1:2                             ; Define tool 0"))
            {
                Assert.AreEqual(CodeType.MCode, code.Type);
                Assert.AreEqual(563, code.MajorNumber);
                Assert.IsNull(code.MinorNumber);
                Assert.AreEqual(3, code.Parameters.Count);
                Assert.AreEqual('P', code.Parameters[0].Letter);
                Assert.AreEqual(0, (int)code.Parameters[0]);
                Assert.AreEqual('D', code.Parameters[1].Letter);
                Assert.AreEqual(new int[] { 0, 1 }, (int[])code.Parameters[1]);
                Assert.AreEqual('H', code.Parameters[2].Letter);
                Assert.AreEqual(new int[] { 1, 2 }, (int[])code.Parameters[2]);
                Assert.AreEqual(" Define tool 0", code.Comment);
            }
        }

        [Test]
        public void ParseM569()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M569 P1.2 S1 T0.5"))
            {
                Assert.AreEqual(CodeType.MCode, code.Type);
                Assert.AreEqual(569, code.MajorNumber);
                Assert.IsNull(code.MinorNumber);
                Assert.AreEqual(CodeFlags.IsLastCode, code.Flags);
                Assert.AreEqual(3, code.Parameters.Count);
                Assert.AreEqual('P', code.Parameters[0].Letter);
                Assert.AreEqual(new DriverId(1, 2), (DriverId)code.Parameters[0]);
                Assert.AreEqual('S', code.Parameters[1].Letter);
                Assert.AreEqual(1, (int)code.Parameters[1]);
                Assert.AreEqual('T', code.Parameters[2].Letter);
                Assert.AreEqual(0.5, code.Parameters[2], 0.0001);
            }
        }

        [Test]
        public void ParseM574()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M574 Y2 S1 P\"io1.in\";comment"))
            {
                Assert.AreEqual(CodeType.MCode, code.Type);
                Assert.AreEqual(574, code.MajorNumber);
                Assert.IsNull(code.MinorNumber);
                Assert.AreEqual(CodeFlags.IsLastCode, code.Flags);
                Assert.AreEqual(3, code.Parameters.Count);
                Assert.AreEqual('Y', code.Parameters[0].Letter);
                Assert.AreEqual(2, (int)code.Parameters[0]);
                Assert.AreEqual('S', code.Parameters[1].Letter);
                Assert.AreEqual(1, (int)code.Parameters[1]);
                Assert.AreEqual('P', code.Parameters[2].Letter);
                Assert.AreEqual("io1.in", (string)code.Parameters[2]);
                Assert.AreEqual("comment", code.Comment);
            }
        }

        [Test]
        public void ParseM587()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M587 S\"TestAp\" P\"Some pass\" I192.168.1.123 J192.168.1.254 K255.255.255.0"))
            {
                Assert.AreEqual(CodeType.MCode, code.Type);
                Assert.AreEqual(587, code.MajorNumber);
                Assert.IsNull(code.MinorNumber);
                Assert.AreEqual(CodeFlags.IsLastCode, code.Flags);
                Assert.AreEqual(5, code.Parameters.Count);
                Assert.AreEqual('S', code.Parameters[0].Letter);
                Assert.AreEqual("TestAp", (string)code.Parameters[0]);
                Assert.AreEqual('P', code.Parameters[1].Letter);
                Assert.AreEqual("Some pass", (string)code.Parameters[1]);
                Assert.AreEqual('I', code.Parameters[2].Letter);
                Assert.AreEqual(IPAddress.Parse("192.168.1.123"), (IPAddress)code.Parameters[2]);
                Assert.AreEqual('J', code.Parameters[3].Letter);
                Assert.AreEqual(IPAddress.Parse("192.168.1.254"), (IPAddress)code.Parameters[3]);
                Assert.AreEqual('K', code.Parameters[4].Letter);
                Assert.AreEqual(IPAddress.Parse("255.255.255.0"), (IPAddress)code.Parameters[4]);
            }
        }

        [Test]
        public void ParseM915()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M915 P2:0.3:1.4 S22"))
            {
                Assert.AreEqual(CodeType.MCode, code.Type);
                Assert.AreEqual(915, code.MajorNumber);
                Assert.IsNull(code.MinorNumber);
                Assert.AreEqual(CodeFlags.IsLastCode, code.Flags);
                Assert.AreEqual(2, code.Parameters.Count);
                Assert.AreEqual('P', code.Parameters[0].Letter);
                DriverId[] driverIds = new DriverId[] { new DriverId(2), new DriverId(3), new DriverId(1, 4) };
                Assert.AreEqual(driverIds, (DriverId[])code.Parameters[0]);
                Assert.AreEqual('S', code.Parameters[1].Letter);
                Assert.AreEqual(22, (int)code.Parameters[1]);
            }
        }

        [Test]
        public void ParseT3()
        {
            foreach (DuetAPI.Commands.Code code in Parse("T3 P4 S\"foo\""))
            {
                Assert.AreEqual(CodeType.TCode, code.Type);
                Assert.AreEqual(3, code.MajorNumber);
                Assert.IsNull(code.MinorNumber);
                Assert.AreEqual(CodeFlags.IsLastCode, code.Flags);
                Assert.AreEqual(2, code.Parameters.Count);
                Assert.AreEqual('P', code.Parameters[0].Letter);
                Assert.AreEqual(4, (int)code.Parameters[0]);
                Assert.AreEqual('S', code.Parameters[1].Letter);
                Assert.AreEqual("foo", (string)code.Parameters[1]);
                Assert.AreEqual("T3 P4 S\"foo\"", code.ToString());
            }
        }

        [Test]
        public void ParseQuotedM32()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M32 \"foo bar.g\""))
            {
                Assert.AreEqual(CodeType.MCode, code.Type);
                Assert.AreEqual(32, code.MajorNumber);
                Assert.AreEqual("foo bar.g", code.GetUnprecedentedString());
            }
        }

        [Test]
        public void ParseUnquotedM32()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M32 foo bar.g"))
            {
                Assert.AreEqual(0, code.Indent);
                Assert.AreEqual(CodeType.MCode, code.Type);
                Assert.AreEqual(32, code.MajorNumber);
                Assert.AreEqual("foo bar.g", code.GetUnprecedentedString());
            }
        }

        [Test]
        public void ParseM584WithExpressions()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M584 E123:{456}"))
            {
                Assert.AreEqual(CodeType.MCode, code.Type);
                Assert.AreEqual(584, code.MajorNumber);
                Assert.IsNull(code.MinorNumber);
                Assert.AreEqual(1, code.Parameters.Count);
                Assert.AreEqual('E', code.Parameters[0].Letter);
                Assert.IsTrue(code.Parameters[0].IsExpression);
                Assert.AreEqual("{123:{456}}", (string)code.Parameters[0]);
            }

            foreach (DuetAPI.Commands.Code code in Parse("M584 E{123}:{456}:789"))
            {
                Assert.AreEqual(CodeType.MCode, code.Type);
                Assert.AreEqual(584, code.MajorNumber);
                Assert.IsNull(code.MinorNumber);
                Assert.AreEqual(1, code.Parameters.Count);
                Assert.AreEqual('E', code.Parameters[0].Letter);
                Assert.IsTrue(code.Parameters[0].IsExpression);
                Assert.AreEqual("{{123}:{456}:789}", (string)code.Parameters[0]);
            }

            foreach (DuetAPI.Commands.Code code in Parse("M584 E{123}:{456}:{789}"))
            {
                Assert.AreEqual(CodeType.MCode, code.Type);
                Assert.AreEqual(584, code.MajorNumber);
                Assert.IsNull(code.MinorNumber);
                Assert.AreEqual(1, code.Parameters.Count);
                Assert.AreEqual('E', code.Parameters[0].Letter);
                Assert.IsTrue(code.Parameters[0].IsExpression);
                Assert.AreEqual("{123}:{456}:{789}", (string)code.Parameters[0]);
            }

            foreach (DuetAPI.Commands.Code code in Parse("M92 E{123,456}"))
            {
                Assert.AreEqual(CodeType.MCode, code.Type);
                Assert.AreEqual(92, code.MajorNumber);
                Assert.IsNull(code.MinorNumber);
                Assert.AreEqual(1, code.Parameters.Count);
                Assert.AreEqual('E', code.Parameters[0].Letter);
                Assert.IsTrue(code.Parameters[0].IsExpression);
                Assert.AreEqual("{123,456}", (string)code.Parameters[0]);
            }

        }

        [Test]
        public void ParseM586WithComment()
        {
            foreach (DuetAPI.Commands.Code code in Parse(" \t M586 P2 S0                               ; Disable Telnet"))
            {
                Assert.AreEqual(5, code.Indent);
                Assert.AreEqual(CodeType.MCode, code.Type);
                Assert.AreEqual(586, code.MajorNumber);
                Assert.IsNull(code.MinorNumber);
                Assert.AreEqual(2, code.Parameters.Count);
                Assert.AreEqual('P', code.Parameters[0].Letter);
                Assert.AreEqual(2, (int)code.Parameters[0]);
                Assert.AreEqual('S', code.Parameters[1].Letter);
                Assert.AreEqual(0, (int)code.Parameters[1]);
                Assert.AreEqual(" Disable Telnet", code.Comment);
            }
        }

        [Test]
        public void ParseG1Absolute()
        {
            foreach (DuetAPI.Commands.Code code in Parse("G53 G1 X3 Y1.25"))
            {
                Assert.AreEqual(CodeFlags.EnforceAbsolutePosition | CodeFlags.IsLastCode, code.Flags);
                Assert.AreEqual(1, code.MajorNumber);
                Assert.IsNull(code.MinorNumber);
                Assert.AreEqual(2, code.Parameters.Count);
                Assert.AreEqual('X', code.Parameters[0].Letter);
                Assert.AreEqual(3, (int)code.Parameters[0]);
                Assert.AreEqual('Y', code.Parameters[1].Letter);
                Assert.AreEqual(1.25, code.Parameters[1], 0.0001);
            }
        }

        [Test]
        public void ParseG1Expression()
        {
            foreach (DuetAPI.Commands.Code code in Parse("G1 X{machine.axes[0].maximum - 10} Y{machine.axes[1].maximum - 10}"))
            {
                Assert.AreEqual(CodeType.GCode, code.Type);
                Assert.AreEqual(1, code.MajorNumber);
                Assert.IsNull(code.MinorNumber);
                Assert.AreEqual(2, code.Parameters.Count);
                Assert.IsTrue(code.Parameters[0].IsExpression);
                Assert.AreEqual('X', code.Parameters[0].Letter);
                Assert.AreEqual("{machine.axes[0].maximum - 10}", (string)code.Parameters[0]);
                Assert.IsTrue(code.Parameters[1].IsExpression);
                Assert.AreEqual('Y', code.Parameters[1].Letter);
                Assert.AreEqual("{machine.axes[1].maximum - 10}", (string)code.Parameters[1]);
            }
        }

        [Test]
        public void ParseM32Expression()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M32 {my.test.value}"))
            {
                Assert.AreEqual(CodeType.MCode, code.Type);
                Assert.AreEqual(32, code.MajorNumber);
                Assert.IsNull(code.MinorNumber);
                Assert.AreEqual(1, code.Parameters.Count);
                Assert.AreEqual('@', code.Parameters[0].Letter);
                Assert.AreEqual(true, code.Parameters[0].IsExpression);
                Assert.AreEqual("{my.test.value}", (string)code.Parameters[0]);
            }
        }

        [Test]
        public void ParseM117()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M117 Hello world!;comment"))
            {
                Assert.AreEqual(CodeType.MCode, code.Type);
                Assert.AreEqual(117, code.MajorNumber);
                Assert.IsNull(code.MinorNumber);
                Assert.AreEqual(1, code.Parameters.Count);
                Assert.AreEqual('@', code.Parameters[0].Letter);
                Assert.IsFalse(code.Parameters[0].IsExpression);
                Assert.AreEqual("Hello world!", (string)code.Parameters[0]);
                Assert.AreEqual("comment", code.Comment);
            }
        }

        [Test]
        public void ParseM117Expression()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M117 { \"Axis \" ^ ( move.axes[0].letter ) ^ \" not homed. Please wait while all axes are homed\" }"))
            {
                Assert.AreEqual(CodeType.MCode, code.Type);
                Assert.AreEqual(117, code.MajorNumber);
                Assert.IsNull(code.MinorNumber);
                Assert.AreEqual(1, code.Parameters.Count);
                Assert.AreEqual('@', code.Parameters[0].Letter);
                Assert.IsTrue(code.Parameters[0].IsExpression);
                Assert.AreEqual("{ \"Axis \" ^ ( move.axes[0].letter ) ^ \" not homed. Please wait while all axes are homed\" }", (string)code.Parameters[0]);
            }
        }

        [Test]
        public void ParseEmptyComments()
        {
            foreach (DuetAPI.Commands.Code code in Parse(";"))
            {
                Assert.AreEqual(CodeType.Comment, code.Type);
                Assert.AreEqual(string.Empty, code.Comment);
            }

            foreach (DuetAPI.Commands.Code code in Parse("()"))
            {
                Assert.AreEqual(CodeType.Comment, code.Type);
                Assert.AreEqual(string.Empty, code.Comment);
            }
        }

        [Test]
        public void ParseLineNumber()
        {
            foreach (DuetAPI.Commands.Code code in Parse("  N123 G1 X5 Y3"))
            {
                Assert.AreEqual(2, code.Indent);
                Assert.AreEqual(123, code.LineNumber);
                Assert.AreEqual(CodeType.GCode, code.Type);
                Assert.AreEqual(1, code.MajorNumber);
                Assert.IsNull(code.MinorNumber);
                Assert.AreEqual(2, code.Parameters.Count);
                Assert.AreEqual('X', code.Parameters[0].Letter);
                Assert.AreEqual(5, (int)code.Parameters[0]);
                Assert.AreEqual('Y', code.Parameters[1].Letter);
                Assert.AreEqual(3, (int)code.Parameters[1]);
            }
        }

        [Test]
        public void ParseIf()
        {
            foreach (DuetAPI.Commands.Code code in Parse("if machine.tool.is.great <= {(0.03 - 0.001) + {foo}} ;some nice comment"))
            {
                Assert.AreEqual(0, code.Indent);
                Assert.AreEqual(KeywordType.If, code.Keyword);
                Assert.AreEqual("machine.tool.is.great <= {(0.03 - 0.001) + {foo}}", code.KeywordArgument);
                Assert.AreEqual("some nice comment", code.Comment);
            }
        }

        [Test]
        public void ParseIf2()
        {
            foreach (DuetAPI.Commands.Code code in Parse("  if {abs(move.calibration.final.deviation - move.calibration.initial.deviation)} < 0.005"))
            {
                Assert.AreEqual(2, code.Indent);
                Assert.AreEqual(KeywordType.If, code.Keyword);
                Assert.AreEqual("{abs(move.calibration.final.deviation - move.calibration.initial.deviation)} < 0.005", code.KeywordArgument);
                Assert.IsNull(code.Comment);
            }
        }

        [Test]
        public void ParseElif()
        {
            foreach (DuetAPI.Commands.Code code in Parse("  elif true"))
            {
                Assert.AreEqual(2, code.Indent);
                Assert.AreEqual(KeywordType.ElseIf, code.Keyword);
                Assert.AreEqual("true", code.KeywordArgument);
            }
        }

        [Test]
        public void ParseElse()
        {
            foreach (DuetAPI.Commands.Code code in Parse("  else"))
            {
                Assert.AreEqual(2, code.Indent);
                Assert.AreEqual(KeywordType.Else, code.Keyword);
                Assert.IsNull(code.KeywordArgument);
            }
        }

        [Test]
        public void ParseWhile()
        {
            foreach (DuetAPI.Commands.Code code in Parse("  while machine.autocal.stddev > 0.04"))
            {
                Assert.AreEqual(2, code.Indent);
                Assert.AreEqual(KeywordType.While, code.Keyword);
                Assert.AreEqual("machine.autocal.stddev > 0.04", code.KeywordArgument);
            }
        }

        [Test]
        public void ParseBreak()
        {
            foreach (DuetAPI.Commands.Code code in Parse("    break"))
            {
                Assert.AreEqual(4, code.Indent);
                Assert.AreEqual(KeywordType.Break, code.Keyword);
                Assert.IsNull(code.KeywordArgument);
            }
        }

        [Test]
        public void ParseContinue()
        {
            foreach (DuetAPI.Commands.Code code in Parse("  continue"))
            {
                Assert.AreEqual(2, code.Indent);
                Assert.AreEqual(KeywordType.Continue, code.Keyword);
                Assert.IsNull(code.KeywordArgument);
            }
        }

        [Test]
        public void ParseAbort()
        {
            foreach (DuetAPI.Commands.Code code in Parse("    abort foo bar"))
            {
                Assert.AreEqual(4, code.Indent);
                Assert.AreEqual(KeywordType.Abort, code.Keyword);
                Assert.AreEqual("foo bar", code.KeywordArgument);
            }
        }

        [Test]
        public void ParseVar()
        {
            foreach (DuetAPI.Commands.Code code in Parse("  var asdf=0.34"))
            {
                Assert.AreEqual(2, code.Indent);
                Assert.AreEqual(KeywordType.Var, code.Keyword);
                Assert.AreEqual("asdf=0.34", code.KeywordArgument);
            }
        }

        [Test]
        public void ParseSet()
        {
            foreach (DuetAPI.Commands.Code code in Parse("  set asdf=\"meh\""))
            {
                Assert.AreEqual(2, code.Indent);
                Assert.AreEqual(KeywordType.Set, code.Keyword);
                Assert.AreEqual("asdf=\"meh\"", code.KeywordArgument);
            }
        }

        [Test]
        public void ParseGlobal()
        {
            foreach (DuetAPI.Commands.Code code in Parse(" \tglobal foo=\"bar\""))
            {
                Assert.AreEqual(4, code.Indent);
                Assert.AreEqual(KeywordType.Global, code.Keyword);
                Assert.AreEqual("foo=\"bar\"", code.KeywordArgument);
            }
        }

        [Test]
        public void ParseEcho()
        {
            foreach (DuetAPI.Commands.Code code in Parse("echo {{3 + 3} + (volumes[0].freeSpace - 4)}"))
            {
                Assert.AreEqual(0, code.Indent);
                Assert.AreEqual(KeywordType.Echo, code.Keyword);
                Assert.AreEqual("{{3 + 3} + (volumes[0].freeSpace - 4)}", code.KeywordArgument);
            }
        }

        [Test]
        public void ParseEchoWithBraces()
        {
            foreach (DuetAPI.Commands.Code code in Parse(" \techo \"debug \" ^ abs(3)"))
            {
                Assert.AreEqual(4, code.Indent);
                Assert.AreEqual(KeywordType.Echo, code.Keyword);
                Assert.AreEqual("\"debug \" ^ abs(3)", code.KeywordArgument);
            }
        }

        [Test]
        public void ParseDynamicT()
        {
            foreach (DuetAPI.Commands.Code code in Parse("T{my.expression} P0"))
            {
                Assert.AreEqual(CodeFlags.IsLastCode, code.Flags);
                Assert.AreEqual(CodeType.TCode, code.Type);
                Assert.IsNull(code.MajorNumber);
                Assert.IsNull(code.MinorNumber);
                Assert.AreEqual(2, code.Parameters.Count);
                Assert.AreEqual('T', code.Parameters[0].Letter);
                Assert.AreEqual("{my.expression}", (string)code.Parameters[0]);
                Assert.AreEqual('P', code.Parameters[1].Letter);
                Assert.AreEqual(0, (int)code.Parameters[1]);
            }
        }

        [Test]
        public void ParseNoSpaceComment()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M84 XYE; disable motors"))
            {
                Assert.AreEqual(CodeFlags.IsLastCode, code.Flags);
                Assert.AreEqual(CodeType.MCode, code.Type);
                Assert.AreEqual(84, code.MajorNumber);
                Assert.IsNull(code.MinorNumber);
                Assert.AreEqual(3, code.Parameters.Count);
                Assert.AreEqual('X', code.Parameters[0].Letter);
                Assert.AreEqual(0, (int)code.Parameters[0]);
                Assert.AreEqual('Y', code.Parameters[1].Letter);
                Assert.AreEqual(0, (int)code.Parameters[1]);
                Assert.AreEqual('E', code.Parameters[2].Letter);
                Assert.AreEqual(0, (int)code.Parameters[2]);
                Assert.AreEqual(" disable motors", code.Comment);
            }
        }

        [Test]
        public async Task SimpleCodes()
        {
            DuetControlServer.Commands.SimpleCode simpleCode = new() { Code = "G91 G1 X5 Y2" };
            List<DuetControlServer.Commands.Code> codes = new();
            await foreach (DuetControlServer.Commands.Code code in simpleCode.ParseAsync())
            {
                codes.Add(code);
            }

            Assert.AreEqual(2, codes.Count);

            Assert.AreEqual(CodeType.GCode, codes[0].Type);
            Assert.AreEqual(91, codes[0].MajorNumber);

            Assert.AreEqual(CodeType.GCode, codes[1].Type);
            Assert.AreEqual(1, codes[1].MajorNumber);
            Assert.AreEqual(2, codes[1].Parameters.Count);
            Assert.AreEqual('X', codes[1].Parameters[0].Letter);
            Assert.AreEqual(5, (int)codes[1].Parameters[0]);
            Assert.AreEqual('Y', codes[1].Parameters[1].Letter);
            Assert.AreEqual(2, (int)codes[1].Parameters[1]);
        }

        [Test]
        public async Task SimpleCodesG53Line()
        {
            DuetControlServer.Commands.SimpleCode simpleCode = new() { Code = "G53 G1 X100 G0 Y200\nG1 Z50" };
            List<DuetControlServer.Commands.Code> codes = new();
            await foreach (DuetControlServer.Commands.Code code in simpleCode.ParseAsync())
            {
                codes.Add(code);
            }
            Assert.AreEqual(3, codes.Count);

            Assert.AreEqual(1, codes[0].MajorNumber);
            Assert.AreEqual(1, codes[0].Parameters.Count);
            Assert.AreEqual('X', codes[0].Parameters[0].Letter);
            Assert.AreEqual(100, (int)codes[0].Parameters[0]);
            Assert.IsTrue(codes[0].Flags.HasFlag(CodeFlags.EnforceAbsolutePosition));

            Assert.AreEqual(0, codes[1].MajorNumber);
            Assert.AreEqual(1, codes[1].Parameters.Count);
            Assert.AreEqual('Y', codes[1].Parameters[0].Letter);
            Assert.AreEqual(200, (int)codes[1].Parameters[0]);
            Assert.IsTrue(codes[1].Flags.HasFlag(CodeFlags.EnforceAbsolutePosition));

            Assert.AreEqual(1, codes[2].MajorNumber);
            Assert.AreEqual(1, codes[2].Parameters.Count);
            Assert.AreEqual('Z', codes[2].Parameters[0].Letter);
            Assert.AreEqual(50, (int)codes[2].Parameters[0]);
            Assert.IsFalse(codes[2].Flags.HasFlag(CodeFlags.EnforceAbsolutePosition));
        }

        [Test]
        public async Task SimpleCodesNL()
        {
            DuetControlServer.Commands.SimpleCode simpleCode = new() { Code = "G91\nG1 X5 Y2" };
            List<DuetControlServer.Commands.Code> codes = new();
            await foreach (DuetControlServer.Commands.Code code in simpleCode.ParseAsync())
            {
                codes.Add(code);
            }

            Assert.AreEqual(2, codes.Count);

            Assert.AreEqual(CodeType.GCode, codes[0].Type);
            Assert.AreEqual(91, codes[0].MajorNumber);

            Assert.AreEqual(CodeType.GCode, codes[1].Type);
            Assert.AreEqual(1, codes[1].MajorNumber);
            Assert.AreEqual(2, codes[1].Parameters.Count);
            Assert.AreEqual('X', codes[1].Parameters[0].Letter);
            Assert.AreEqual(5, (int)codes[1].Parameters[0]);
            Assert.AreEqual('Y', codes[1].Parameters[1].Letter);
            Assert.AreEqual(2, (int)codes[1].Parameters[1]);
        }

        [Test]
        public async Task SimpleCodesIndented()
        {
            DuetControlServer.Commands.SimpleCode simpleCode = new() { Code = "    G1 X5 Y5 G1 X10 Y10\nG1 X15 Y15" };
            List<DuetControlServer.Commands.Code> codes = new();
            await foreach (DuetControlServer.Commands.Code code in simpleCode.ParseAsync())
            {
                codes.Add(code);
            }

            Assert.AreEqual(3, codes.Count);

            Assert.AreEqual(CodeType.GCode, codes[0].Type);
            Assert.AreEqual(CodeFlags.None, codes[0].Flags);
            Assert.AreEqual(4, codes[0].Indent);
            Assert.AreEqual(1, codes[0].MajorNumber);
            Assert.AreEqual(2, codes[0].Parameters.Count);
            Assert.AreEqual('X', codes[0].Parameters[0].Letter);
            Assert.AreEqual(5, (int)codes[0].Parameters[0]);
            Assert.AreEqual('Y', codes[0].Parameters[1].Letter);
            Assert.AreEqual(5, (int)codes[0].Parameters[1]);

            Assert.AreEqual(CodeType.GCode, codes[1].Type);
            Assert.AreEqual(CodeFlags.IsLastCode, codes[1].Flags);
            Assert.AreEqual(4, codes[1].Indent);
            Assert.AreEqual(1, codes[1].MajorNumber);
            Assert.AreEqual(2, codes[1].Parameters.Count);
            Assert.AreEqual('X', codes[1].Parameters[0].Letter);
            Assert.AreEqual(10, (int)codes[1].Parameters[0]);
            Assert.AreEqual('Y', codes[1].Parameters[1].Letter);
            Assert.AreEqual(10, (int)codes[1].Parameters[1]);

            Assert.AreEqual(CodeType.GCode, codes[2].Type);
            Assert.AreEqual(CodeFlags.IsLastCode, codes[2].Flags);
            Assert.AreEqual(0, codes[2].Indent);
            Assert.AreEqual(1, codes[2].MajorNumber);
            Assert.AreEqual(2, codes[2].Parameters.Count);
            Assert.AreEqual('X', codes[2].Parameters[0].Letter);
            Assert.AreEqual(15, (int)codes[2].Parameters[0]);
            Assert.AreEqual('Y', codes[2].Parameters[1].Letter);
            Assert.AreEqual(15, (int)codes[2].Parameters[1]);
        }

        [Test]
        public async Task ParseAsync()
        {
            string codeString = "G53 G1 X0 Y5 F3000 G0 X5 Y10";
            byte[] codeBytes = Encoding.UTF8.GetBytes(codeString);
            using (MemoryStream memoryStream = new(codeBytes))
            {
                using StreamReader reader = new(memoryStream);
                CodeParserBuffer buffer = new(128, true);
                DuetAPI.Commands.Code code = new() { LineNumber = 1 };

                await DuetAPI.Commands.Code.ParseAsync(reader, code, buffer);
                Assert.AreEqual(CodeType.GCode, code.Type);
                Assert.AreEqual(1, code.MajorNumber);
                Assert.AreEqual(CodeFlags.EnforceAbsolutePosition, code.Flags);
                Assert.AreEqual(1, code.LineNumber);
                Assert.AreEqual(3, code.Parameters.Count);
                Assert.AreEqual(0, (int)code.Parameter('X'));
                Assert.AreEqual(5, (int)code.Parameter('Y'));
                Assert.AreEqual(3000, (int)code.Parameter('F'));

                code.Reset();
                await DuetAPI.Commands.Code.ParseAsync(reader, code, buffer);
                Assert.AreEqual(CodeType.GCode, code.Type);
                Assert.AreEqual(0, code.MajorNumber);
                Assert.AreEqual(CodeFlags.EnforceAbsolutePosition | CodeFlags.IsLastCode, code.Flags);
                Assert.AreEqual(1, code.LineNumber);
                Assert.AreEqual(2, code.Parameters.Count);
                Assert.AreEqual(5, (int)code.Parameter('X'));
                Assert.AreEqual(10, (int)code.Parameter('Y'));
            }

            codeString = "G1 X1 Y5 F3000\nG1 X5 F300\nG0 Y40";
            codeBytes = Encoding.UTF8.GetBytes(codeString);
            using (MemoryStream memoryStream = new(codeBytes))
            {
                using StreamReader reader = new(memoryStream);
                CodeParserBuffer buffer = new(128, true);

                DuetAPI.Commands.Code code = new() { LineNumber = 0 };
                await DuetAPI.Commands.Code.ParseAsync(reader, code, buffer);

                code.Reset();
                await DuetAPI.Commands.Code.ParseAsync(reader, code, buffer);

                code.Reset();
                await DuetAPI.Commands.Code.ParseAsync(reader, code, buffer);

                Assert.AreEqual(CodeType.GCode, code.Type);
                Assert.AreEqual(0, code.MajorNumber);
                Assert.AreEqual(3, code.LineNumber);
            }


            codeString = "G1 X1 Y5 F3000\nX5 F300\nY40";
            codeBytes = Encoding.UTF8.GetBytes(codeString);
            using (MemoryStream memoryStream = new(codeBytes))
            {
                using StreamReader reader = new(memoryStream);
                CodeParserBuffer buffer = new(128, true) { MayRepeatCode = true };
                DuetAPI.Commands.Code code = new() { LineNumber = 0 };

                await DuetAPI.Commands.Code.ParseAsync(reader, code, buffer);
                Assert.AreEqual(1, code.LineNumber);
                Assert.AreEqual(CodeType.GCode, code.Type);
                Assert.AreEqual(1, code.MajorNumber);
                Assert.AreEqual(3, code.Parameters.Count);
                Assert.AreEqual(1, (int)code.Parameter('X'));
                Assert.AreEqual(5, (int)code.Parameter('Y'));
                Assert.AreEqual(3000, (int)code.Parameter('F'));

                code.Reset();
                await DuetAPI.Commands.Code.ParseAsync(reader, code, buffer);
                Assert.AreEqual(CodeType.GCode, code.Type);
                Assert.AreEqual(1, code.MajorNumber);
                Assert.AreEqual(2, code.LineNumber);
                Assert.AreEqual(2, code.Parameters.Count);
                Assert.AreEqual(5, (int)code.Parameter('X'));
                Assert.AreEqual(300, (int)code.Parameter('F'));

                code.Reset();
                await DuetAPI.Commands.Code.ParseAsync(reader, code, buffer);
                Assert.AreEqual(3, code.LineNumber);
                Assert.AreEqual(CodeType.GCode, code.Type);
                Assert.AreEqual(1, code.MajorNumber);
                Assert.AreEqual(1, code.Parameters.Count);
                Assert.AreEqual(40, (int)code.Parameter('Y'));
            }

            codeString = "G1 X1 Y5 F3000\n  G53 G1 X5 F300\n    G53 G0 Y40 G1 Z50\n  G4 S3\nG1 Z3";
            codeBytes = Encoding.UTF8.GetBytes(codeString);
            using (MemoryStream memoryStream = new(codeBytes))
            {
                using StreamReader reader = new(memoryStream);
                CodeParserBuffer buffer = new(128, true);

                DuetAPI.Commands.Code code = new() { LineNumber = 0 };
                await DuetAPI.Commands.Code.ParseAsync(reader, code, buffer);
                Assert.AreEqual(CodeFlags.IsLastCode, code.Flags);
                Assert.AreEqual(0, code.Indent);
                Assert.AreEqual(1, code.LineNumber);

                code.Reset();
                await DuetAPI.Commands.Code.ParseAsync(reader, code, buffer);
                Assert.AreEqual(CodeFlags.EnforceAbsolutePosition | CodeFlags.IsLastCode, code.Flags);
                Assert.AreEqual(2, code.Indent);
                Assert.AreEqual(2, code.LineNumber);

                code.Reset();
                await DuetAPI.Commands.Code.ParseAsync(reader, code, buffer);
                Assert.AreEqual(CodeFlags.EnforceAbsolutePosition, code.Flags);
                Assert.AreEqual(4, code.Indent);
                Assert.AreEqual(3, code.LineNumber);

                code.Reset();
                await DuetAPI.Commands.Code.ParseAsync(reader, code, buffer);
                Assert.AreEqual(CodeFlags.EnforceAbsolutePosition | CodeFlags.IsLastCode, code.Flags);
                Assert.AreEqual(4, code.Indent);
                Assert.AreEqual(3, code.LineNumber);

                code.Reset();
                await DuetAPI.Commands.Code.ParseAsync(reader, code, buffer);
                Assert.AreEqual(CodeFlags.IsLastCode, code.Flags);
                Assert.AreEqual(2, code.Indent);
                Assert.AreEqual(4, code.LineNumber);

                code.Reset();
                await DuetAPI.Commands.Code.ParseAsync(reader, code, buffer);
                Assert.AreEqual(CodeFlags.IsLastCode, code.Flags);
                Assert.AreEqual(0, code.Indent);
                Assert.AreEqual(5, code.LineNumber);
            }

            codeString = "M291 P\"Please go to <a href=\"\"https://www.duet3d.com/StartHere\"\" target=\"\"_blank\"\">this</a> page for further instructions on how to set it up.\" R\"Welcome to your new Duet 3!\" S1 T0";
            codeBytes = Encoding.UTF8.GetBytes(codeString);
            using (MemoryStream memoryStream = new(codeBytes))
            {
                using StreamReader reader = new(memoryStream);
                CodeParserBuffer buffer = new(128, true);

                DuetAPI.Commands.Code code = new();
                await DuetAPI.Commands.Code.ParseAsync(reader, code, buffer);
                Assert.AreEqual(CodeType.MCode, code.Type);
                Assert.AreEqual(291, code.MajorNumber);
                Assert.AreEqual("Please go to <a href=\"https://www.duet3d.com/StartHere\" target=\"_blank\">this</a> page for further instructions on how to set it up.", (string)code.Parameter('P'));
                Assert.AreEqual("Welcome to your new Duet 3!", (string)code.Parameter('R'));
                Assert.AreEqual(1, (int)code.Parameter('S'));
                Assert.AreEqual(0, (int)code.Parameter('T'));
            }
        }

        public static IEnumerable<DuetAPI.Commands.Code> Parse(string code)
        {
            yield return new DuetAPI.Commands.Code(code);

            byte[] codeBytes = Encoding.UTF8.GetBytes(code);
            using MemoryStream memoryStream = new(codeBytes);
            using StreamReader reader = new(memoryStream);
            CodeParserBuffer buffer = new(128, true);
            DuetAPI.Commands.Code codeObj = new();
            DuetAPI.Commands.Code.ParseAsync(reader, codeObj, buffer).AsTask().Wait();
            yield return codeObj;
        }
    }
}
