using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Utility;
using NUnit.Framework;

using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace UnitTests.Commands;

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
            Assert.That(code.Type, Is.EqualTo(CodeType.GCode));
            Assert.That(code.MajorNumber, Is.EqualTo(29));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Parameters.Count, Is.EqualTo(1));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('S'));
            Assert.That(code.GetInt('S', 0), Is.EqualTo(1));
        }
    }

    [Test]
    public void ParseG53()
    {
        foreach (DuetAPI.Commands.Code code in Parse("G53"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.GCode));
            Assert.That(code.MajorNumber, Is.EqualTo(53));
            Assert.That(code.MinorNumber, Is.Null);
        }
    }

    [Test]
    public void ParseG54()
    {
        foreach (DuetAPI.Commands.Code code in Parse("G54.6"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.GCode));
            Assert.That(code.MajorNumber, Is.EqualTo(54));
            Assert.That(code.MinorNumber, Is.EqualTo(6));
        }
    }

    [Test]
    public void ParseG92()
    {
        foreach (DuetAPI.Commands.Code code in Parse("G92 X0 Y0 Z0"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.GCode));
            Assert.That(code.MajorNumber, Is.EqualTo(92));
            Assert.That(code.MinorNumber, Is.Null);

            Assert.That(code.Parameters.Count, Is.EqualTo(3));

            Assert.That(code.Parameters[0].Letter, Is.EqualTo('X'));
            Assert.That((int)code.Parameters[0], Is.EqualTo(0));
            Assert.That(code.Parameters[1].Letter, Is.EqualTo('Y'));
            Assert.That((int)code.Parameters[1], Is.EqualTo(0));
            Assert.That(code.Parameters[2].Letter, Is.EqualTo('Z'));
            Assert.That((int)code.Parameters[2], Is.EqualTo(0));
        }
    }

    [Test]
    public void ParseM32()
    {
        foreach (DuetAPI.Commands.Code code in Parse("M32 some fancy  file.g"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(32));
            Assert.That(code.GetUnprecedentedString(), Is.EqualTo("some fancy  file.g"));
        }
    }

    [Test]
    public void ParseM92()
    {
        foreach (DuetAPI.Commands.Code code in Parse("M92 E810:810:407:407"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(92));

            Assert.That(code.Parameters.Count, Is.EqualTo(1));

            int[] steps = [810, 810, 407, 407];
            Assert.That(code.GetIntArray('E')!, Is.EqualTo(steps));
        }
    }

    [Test]
    public void ParseM98()
    {
        foreach (DuetAPI.Commands.Code code in Parse("M98 P\"config.g\""))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(98));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Parameters.Count, Is.EqualTo(1));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('P'));
            Assert.That((string)code.Parameters[0], Is.EqualTo("config.g"));
        }
    }

    [Test]
    public void ParseM106()
    {
        foreach (DuetAPI.Commands.Code code in Parse("M106 P1 C\"Fancy \"\" Fan\" H-1 S0.5"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(106));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Parameters.Count, Is.EqualTo(4));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('P'));
            Assert.That((int)code.Parameters[0], Is.EqualTo(1));
            Assert.That(code.Parameters[1].Letter, Is.EqualTo('C'));
            Assert.That((string)code.Parameters[1], Is.EqualTo("Fancy \" Fan"));
            Assert.That(code.Parameters[2].Letter, Is.EqualTo('H'));
            Assert.That((int)code.Parameters[2], Is.EqualTo(-1));
            Assert.That(code.Parameters[3].Letter, Is.EqualTo('S'));
            Assert.That((float)code.Parameters[3], Is.EqualTo(0.5).Within(0.0001));

            TestContext.Out.WriteLine(JsonSerializer.Serialize(code, typeof(DuetAPI.Commands.Code), new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [Test]
    public void ParseEmptyM117()
    {
        foreach (DuetAPI.Commands.Code code in Parse("M117 \"\""))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(117));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Parameters.Count, Is.EqualTo(1));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('@'));
            Assert.That((string)code.Parameters[0], Is.EqualTo(string.Empty));
        }
    }

    [Test]
    public void ParseM122DSF()
    {
        foreach (DuetAPI.Commands.Code code in Parse("M122 \"DSF\""))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(122));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Parameters.Count, Is.EqualTo(1));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('@'));
            Assert.That((string)code.Parameters[0], Is.EqualTo("DSF"));
        }
    }

#if false
        [Test]
        public void ParseM260()
        {
            foreach (DuetAPI.Commands.Code code in Parse("M260 A0xF1 B0"))
            {
                Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
                Assert.That(code.MajorNumber, Is.EqualTo(260));
                Assert.That(code.Parameters.Count, Is.EqualTo(2));
                Assert.That(code.Parameters[0].Letter, Is.EqualTo('A'));
                Assert.That((int)code.Parameters[0], Is.EqualTo(0xF1));
                Assert.That(code.Parameters[1].Letter, Is.EqualTo('B'));
                Assert.That((int)code.Parameters[1], Is.EqualTo(0));
            }

            foreach (DuetAPI.Commands.Code code in Parse("M260 A0XF1 B0"))
            {
                Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
                Assert.That(code.MajorNumber, Is.EqualTo(260));
                Assert.That(code.Parameters.Count, Is.EqualTo(2));
                Assert.That(code.Parameters[0].Letter, Is.EqualTo('A'));
                Assert.That((int)code.Parameters[0], Is.EqualTo(0xF1));
                Assert.That(code.Parameters[1].Letter, Is.EqualTo('B'));
                Assert.That((int)code.Parameters[1], Is.EqualTo(0));
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
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(302));
            Assert.That(code.Parameters.Count, Is.EqualTo(2));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('D'));
            Assert.That((string)code.Parameters[0], Is.EqualTo("dummy"));
            Assert.That(code.Parameters[1].Letter, Is.EqualTo('P'));
            Assert.That((int)code.Parameters[1], Is.EqualTo(1));
        }
    }

    [Test]
    public void ParseM563()
    {
        foreach (DuetAPI.Commands.Code code in Parse("M563 P0 D0:1 H1:2                             ; Define tool 0"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(563));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Parameters.Count, Is.EqualTo(3));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('P'));
            Assert.That((int)code.Parameters[0], Is.EqualTo(0));
            Assert.That(code.Parameters[1].Letter, Is.EqualTo('D'));
            Assert.That((int[])code.Parameters[1], Is.EqualTo(new int[] { 0, 1 }));
            Assert.That(code.Parameters[2].Letter, Is.EqualTo('H'));
            Assert.That((int[])code.Parameters[2], Is.EqualTo(new int[] { 1, 2 }));
            Assert.That(code.Comment, Is.EqualTo(" Define tool 0"));
        }
    }

    [Test]
    public void ParseM569()
    {
        foreach (DuetAPI.Commands.Code code in Parse("M569 P1.2 S1 T0.5"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(569));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Flags, Is.EqualTo(CodeFlags.IsLastCode));
            Assert.That(code.Parameters.Count, Is.EqualTo(3));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('P'));
            Assert.That((DriverId)code.Parameters[0], Is.EqualTo(new DriverId(1, 2)));
            Assert.That(code.Parameters[1].Letter, Is.EqualTo('S'));
            Assert.That((int)code.Parameters[1], Is.EqualTo(1));
            Assert.That(code.Parameters[2].Letter, Is.EqualTo('T'));
            Assert.That((float)code.Parameters[2], Is.EqualTo(0.5).Within(0.0001));
        }
    }

    [Test]
    public void ParseM569Array()
    {
        foreach (DuetAPI.Commands.Code code in Parse("M569 P1.2:3.4 S1 T0.5"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(569));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Flags, Is.EqualTo(CodeFlags.IsLastCode));
            Assert.That(code.Parameters.Count, Is.EqualTo(3));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('P'));
            Assert.That((DriverId[])code.Parameters[0], Is.EqualTo(new DriverId[] { new(1, 2), new(3, 4) }));
            Assert.That(code.Parameters[1].Letter, Is.EqualTo('S'));
            Assert.That((int)code.Parameters[1], Is.EqualTo(1));
            Assert.That(code.Parameters[2].Letter, Is.EqualTo('T'));
            Assert.That((float)code.Parameters[2], Is.EqualTo(0.5).Within(0.0001));
        }
    }

    [Test]
    public void ParseM574()
    {
        foreach (DuetAPI.Commands.Code code in Parse("M574 Y2 S1 P\"io1.in\";comment"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(574));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Flags, Is.EqualTo(CodeFlags.IsLastCode));
            Assert.That(code.Parameters.Count, Is.EqualTo(3));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('Y'));
            Assert.That((int)code.Parameters[0], Is.EqualTo(2));
            Assert.That(code.Parameters[1].Letter, Is.EqualTo('S'));
            Assert.That((int)code.Parameters[1], Is.EqualTo(1));
            Assert.That(code.Parameters[2].Letter, Is.EqualTo('P'));
            Assert.That((string)code.Parameters[2], Is.EqualTo("io1.in"));
            Assert.That(code.Comment, Is.EqualTo("comment"));
        }
    }

    [Test]
    public void ParseM587()
    {
        foreach (DuetAPI.Commands.Code code in Parse("M587 S\"TestAp\" P\"Some pass\" I192.168.1.123 J192.168.1.254 K255.255.255.0"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(587));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Flags, Is.EqualTo(CodeFlags.IsLastCode));
            Assert.That(code.Parameters.Count, Is.EqualTo(5));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('S'));
            Assert.That((string)code.Parameters[0], Is.EqualTo("TestAp"));
            Assert.That(code.Parameters[1].Letter, Is.EqualTo('P'));
            Assert.That((string)code.Parameters[1], Is.EqualTo("Some pass"));
            Assert.That(code.Parameters[2].Letter, Is.EqualTo('I'));
            Assert.That((IPAddress)code.Parameters[2], Is.EqualTo(IPAddress.Parse("192.168.1.123")));
            Assert.That(code.Parameters[3].Letter, Is.EqualTo('J'));
            Assert.That((IPAddress)code.Parameters[3], Is.EqualTo(IPAddress.Parse("192.168.1.254")));
            Assert.That(code.Parameters[4].Letter, Is.EqualTo('K'));
            Assert.That((IPAddress)code.Parameters[4], Is.EqualTo(IPAddress.Parse("255.255.255.0")));
        }
    }

    [Test]
    public void ParseM915()
    {
        foreach (DuetAPI.Commands.Code code in Parse("M915 P2:0.3:1.4 S22"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(915));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Flags, Is.EqualTo(CodeFlags.IsLastCode));
            Assert.That(code.Parameters.Count, Is.EqualTo(2));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('P'));
            DriverId[] driverIds = [new(2), new(3), new(1, 4)];
            Assert.That((DriverId[])code.Parameters[0], Is.EqualTo(driverIds));
            Assert.That(code.Parameters[1].Letter, Is.EqualTo('S'));
            Assert.That((int)code.Parameters[1], Is.EqualTo(22));
        }
    }

    [Test]
    public void ParseT3()
    {
        foreach (DuetAPI.Commands.Code code in Parse("T3 P4 S\"foo\""))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.TCode));
            Assert.That(code.MajorNumber, Is.EqualTo(3));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Flags, Is.EqualTo(CodeFlags.IsLastCode));
            Assert.That(code.Parameters.Count, Is.EqualTo(2));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('P'));
            Assert.That((int)code.Parameters[0], Is.EqualTo(4));
            Assert.That(code.Parameters[1].Letter, Is.EqualTo('S'));
            Assert.That((string)code.Parameters[1], Is.EqualTo("foo"));
            Assert.That(code.ToString(), Is.EqualTo("T3 P4 S\"foo\""));
        }
    }

    [Test]
    public void ParseQuotedM32()
    {
        foreach (DuetAPI.Commands.Code code in Parse("M32 \"foo bar.g\""))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(32));
            Assert.That(code.GetUnprecedentedString(), Is.EqualTo("foo bar.g"));
        }
    }


    [Test]
    public void ParseChar()
    {
        foreach (DuetAPI.Commands.Code code in Parse("M1234 P'{' S1"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(1234));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Flags, Is.EqualTo(CodeFlags.IsLastCode));
            Assert.That(code.Parameters.Count, Is.EqualTo(2));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('P'));
            Assert.That((string)code.Parameters[0], Is.EqualTo("{"));
            Assert.That(code.Parameters[1].Letter, Is.EqualTo('S'));
            Assert.That((int)code.Parameters[1], Is.EqualTo(1));
        }
    }

    [Test]
    public void ParseApostropheM32()
    {
        foreach (DuetAPI.Commands.Code code in Parse("M32 \"C ''t H, , . ., ''T H.gcode\""))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(32));
            Assert.That(code.GetUnprecedentedString(), Is.EqualTo("C 't H, , . ., 'T H.gcode"));
        }
    }

    [Test]
    public void ParseUnquotedM32()
    {
        foreach (DuetAPI.Commands.Code code in Parse("M32 foo bar.g"))
        {
            Assert.That(code.Indent, Is.EqualTo(0));
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(32));
            Assert.That(code.GetUnprecedentedString(), Is.EqualTo("foo bar.g"));
        }
    }

    [Test]
    public void ParseM584WithExpressions()
    {
        foreach (DuetAPI.Commands.Code code in Parse("M584 E123:{456} 'f7.8 'g9.0"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(584));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Parameters.Count, Is.EqualTo(3));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('E'));
            Assert.That(code.Parameters[0].IsExpression, Is.True);
            Assert.That((string)code.Parameters[0], Is.EqualTo("{123:{456}}"));
            Assert.That(code.Parameters[1].Letter, Is.EqualTo('f'));
            Assert.That((DriverId)code.Parameters[1], Is.EqualTo(new DriverId(7, 8)));
            Assert.That(code.Parameters[2].Letter, Is.EqualTo('g'));
            Assert.That((DriverId)code.Parameters[2], Is.EqualTo(new DriverId(9, 0)));
        }

        foreach (DuetAPI.Commands.Code code in Parse("M584 E{123}:{456}:789"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(584));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Parameters.Count, Is.EqualTo(1));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('E'));
            Assert.That(code.Parameters[0].IsExpression, Is.True);
            Assert.That((string)code.Parameters[0], Is.EqualTo("{{123}:{456}:789}"));
        }

        foreach (DuetAPI.Commands.Code code in Parse("M584 E{123}:{456}:{789}"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(584));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Parameters.Count, Is.EqualTo(1));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('E'));
            Assert.That(code.Parameters[0].IsExpression, Is.True);
            Assert.That((string)code.Parameters[0], Is.EqualTo("{123}:{456}:{789}"));
        }

        foreach (DuetAPI.Commands.Code code in Parse("M92 E{123,456}"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(92));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Parameters.Count, Is.EqualTo(1));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('E'));
            Assert.That(code.Parameters[0].IsExpression, Is.True);
            Assert.That((string)code.Parameters[0], Is.EqualTo("{123,456}"));
        }

    }

    [Test]
    public void ParseM586WithComment()
    {
        foreach (DuetAPI.Commands.Code code in Parse(" \t M586 P2 S0                               ; Disable Telnet"))
        {
            Assert.That(code.Indent, Is.EqualTo(5));
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(586));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Parameters.Count, Is.EqualTo(2));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('P'));
            Assert.That((int)code.Parameters[0], Is.EqualTo(2));
            Assert.That(code.Parameters[1].Letter, Is.EqualTo('S'));
            Assert.That((int)code.Parameters[1], Is.EqualTo(0));
            Assert.That(code.Comment, Is.EqualTo(" Disable Telnet"));
        }
    }

    [Test]
    public void ParseG1Absolute()
    {
        foreach (DuetAPI.Commands.Code code in Parse("G53 G1 X3 Y1.25 A2 'a3 b4"))
        {
            Assert.That(code.Flags, Is.EqualTo(CodeFlags.EnforceAbsolutePosition | CodeFlags.IsLastCode));
            Assert.That(code.MajorNumber, Is.EqualTo(1));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Parameters.Count, Is.EqualTo(5));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('X'));
            Assert.That((int)code.Parameters[0], Is.EqualTo(3));
            Assert.That(code.Parameters[1].Letter, Is.EqualTo('Y'));
            Assert.That((float)code.Parameters[1], Is.EqualTo(1.25).Within(0.0001));
            Assert.That(code.Parameters[2].Letter, Is.EqualTo('A'));
            Assert.That((float)code.Parameters[2], Is.EqualTo(2).Within(0.0001));
            Assert.That(code.Parameters[3].Letter, Is.EqualTo('a'));
            Assert.That((float)code.Parameters[3], Is.EqualTo(3).Within(0.0001));
            Assert.That(code.Parameters[4].Letter, Is.EqualTo('B'));
            Assert.That((float)code.Parameters[4], Is.EqualTo(4).Within(0.0001));
        }
    }

    [Test]
    public void ParseG1Expression()
    {
        foreach (DuetAPI.Commands.Code code in Parse("G1 X{machine.axes[0].maximum - 10} Y{machine.axes[1].maximum - 10}"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.GCode));
            Assert.That(code.MajorNumber, Is.EqualTo(1));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Parameters.Count, Is.EqualTo(2));
            Assert.That(code.Parameters[0].IsExpression, Is.True);
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('X'));
            Assert.That((string)code.Parameters[0], Is.EqualTo("{machine.axes[0].maximum - 10}"));
            Assert.That(code.Parameters[1].IsExpression, Is.True);
            Assert.That(code.Parameters[1].Letter, Is.EqualTo('Y'));
            Assert.That((string)code.Parameters[1], Is.EqualTo("{machine.axes[1].maximum - 10}"));
        }
    }

    [Test]
    public void ParseM32Expression()
    {
        foreach (DuetAPI.Commands.Code code in Parse("M32 {my.test.value}"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(32));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Parameters.Count, Is.EqualTo(1));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('@'));
            Assert.That(code.Parameters[0].IsExpression, Is.EqualTo(true));
            Assert.That((string)code.Parameters[0], Is.EqualTo("{my.test.value}"));
        }
    }

    [Test]
    public void ParseM117()
    {
        foreach (DuetAPI.Commands.Code code in Parse("M117 Hello world!;comment"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(117));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Parameters.Count, Is.EqualTo(1));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('@'));
            Assert.That(code.Parameters[0].IsExpression, Is.False);
            Assert.That((string)code.Parameters[0], Is.EqualTo("Hello world!"));
            Assert.That(code.Comment, Is.EqualTo("comment"));
        }
    }

    [Test]
    public void ParseM118Unicode()
    {
        foreach (DuetAPI.Commands.Code code in Parse("M118 P\"💡 - LEDs on\""))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(118));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Parameters.Count, Is.EqualTo(1));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('P'));
            Assert.That(code.Parameters[0].IsExpression, Is.False);
            Assert.That((string)code.Parameters[0], Is.EqualTo("💡 - LEDs on"));
        }
    }

    [Test]
    public void ParseM117Expression()
    {
        foreach (DuetAPI.Commands.Code code in Parse("M117 { \"Axis \" ^ ( move.axes[0].letter ) ^ \" not homed. Please wait while all axes are homed\" }"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(117));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Parameters.Count, Is.EqualTo(1));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('@'));
            Assert.That(code.Parameters[0].IsExpression, Is.True);
            Assert.That((string)code.Parameters[0], Is.EqualTo("{ \"Axis \" ^ ( move.axes[0].letter ) ^ \" not homed. Please wait while all axes are homed\" }"));
        }
    }

    [Test]
    public void ParseEmptyComments()
    {
        foreach (DuetAPI.Commands.Code code in Parse(";"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.Comment));
            Assert.That(code.Comment, Is.EqualTo(string.Empty));
        }

        foreach (DuetAPI.Commands.Code code in Parse("()"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.Comment));
            Assert.That(code.Comment, Is.EqualTo(string.Empty));
        }
    }

    [Test]
    public void ParseLineNumber()
    {
        foreach (DuetAPI.Commands.Code code in Parse("  N123 G1 X5 Y3"))
        {
            Assert.That(code.Indent, Is.EqualTo(2));
            Assert.That(code.LineNumber, Is.EqualTo(123));
            Assert.That(code.Type, Is.EqualTo(CodeType.GCode));
            Assert.That(code.MajorNumber, Is.EqualTo(1));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Parameters.Count, Is.EqualTo(2));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('X'));
            Assert.That((int)code.Parameters[0], Is.EqualTo(5));
            Assert.That(code.Parameters[1].Letter, Is.EqualTo('Y'));
            Assert.That((int)code.Parameters[1], Is.EqualTo(3));
        }
    }

    [Test]
    public void ParseIf()
    {
        foreach (DuetAPI.Commands.Code code in Parse("if machine.tool.is.great <= {(0.03 - 0.001) + {foo}} ;some nice comment"))
        {
            Assert.That(code.Indent, Is.EqualTo(0));
            Assert.That(code.Type, Is.EqualTo(CodeType.Keyword));
            Assert.That(code.Keyword, Is.EqualTo(KeywordType.If));
            Assert.That(code.KeywordArgument, Is.EqualTo("machine.tool.is.great <= {(0.03 - 0.001) + {foo}}"));
            Assert.That(code.Comment, Is.EqualTo("some nice comment"));
        }
    }

    [Test]
    public void ParseIf2()
    {
        foreach (DuetAPI.Commands.Code code in Parse("  if {abs(move.calibration.final.deviation - move.calibration.initial.deviation)} < 0.005"))
        {
            Assert.That(code.Indent, Is.EqualTo(2));
            Assert.That(code.Type, Is.EqualTo(CodeType.Keyword));
            Assert.That(code.Keyword, Is.EqualTo(KeywordType.If));
            Assert.That(code.KeywordArgument, Is.EqualTo("{abs(move.calibration.final.deviation - move.calibration.initial.deviation)} < 0.005"));
            Assert.That(code.Comment, Is.Null);
        }
    }

    [Test]
    public void ParseElif()
    {
        foreach (DuetAPI.Commands.Code code in Parse("  elif true"))
        {
            Assert.That(code.Indent, Is.EqualTo(2));
            Assert.That(code.Type, Is.EqualTo(CodeType.Keyword));
            Assert.That(code.Keyword, Is.EqualTo(KeywordType.ElseIf));
            Assert.That(code.KeywordArgument, Is.EqualTo("true"));
        }
    }

    [Test]
    public void ParseElse()
    {
        foreach (DuetAPI.Commands.Code code in Parse("  else"))
        {
            Assert.That(code.Indent, Is.EqualTo(2));
            Assert.That(code.Type, Is.EqualTo(CodeType.Keyword));
            Assert.That(code.Keyword, Is.EqualTo(KeywordType.Else));
            Assert.That(code.KeywordArgument, Is.Null);
        }
    }

    [Test]
    public void ParseWhile()
    {
#if false
            foreach (DuetAPI.Commands.Code code in Parse("  while machine.autocal.stddev > 0.04"))
            {
                Assert.That(code.Indent, Is.EqualTo(2));
                Assert.That(code.Type, Is.EqualTo(CodeType.Keyword));
                Assert.That(code.Keyword, Is.EqualTo(KeywordType.While));
                Assert.That(code.KeywordArgument, Is.EqualTo("machine.autocal.stddev > 0.04"));
            }

            foreach (DuetAPI.Commands.Code code in Parse("  while var.i < var.N"))
            {
                Assert.That(code.Indent, Is.EqualTo(2));
                Assert.That(code.Type, Is.EqualTo(CodeType.Keyword));
                Assert.That(code.Keyword, Is.EqualTo(KeywordType.While));
                Assert.That(code.KeywordArgument, Is.EqualTo("var.i < var.N"));
            }
#endif

        foreach (DuetAPI.Commands.Code code in Parse("  while(var.i < var.N)"))
        {
            Assert.That(code.Indent, Is.EqualTo(2));
            Assert.That(code.Type, Is.EqualTo(CodeType.Keyword));
            Assert.That(code.Keyword, Is.EqualTo(KeywordType.While));
            Assert.That(code.KeywordArgument, Is.EqualTo("(var.i < var.N)"));
        }
    }

    [Test]
    public void ParseBreak()
    {
        foreach (DuetAPI.Commands.Code code in Parse("    break"))
        {
            Assert.That(code.Indent, Is.EqualTo(4));
            Assert.That(code.Type, Is.EqualTo(CodeType.Keyword));
            Assert.That(code.Keyword, Is.EqualTo(KeywordType.Break));
            Assert.That(code.KeywordArgument, Is.Null);
        }
    }

    [Test]
    public void ParseContinue()
    {
        foreach (DuetAPI.Commands.Code code in Parse("  continue"))
        {
            Assert.That(code.Indent, Is.EqualTo(2));
            Assert.That(code.Type, Is.EqualTo(CodeType.Keyword));
            Assert.That(code.Keyword, Is.EqualTo(KeywordType.Continue));
            Assert.That(code.KeywordArgument, Is.Null);
        }
    }

    [Test]
    public void ParseAbort()
    {
        foreach (DuetAPI.Commands.Code code in Parse("    abort foo bar"))
        {
            Assert.That(code.Indent, Is.EqualTo(4));
            Assert.That(code.Type, Is.EqualTo(CodeType.Keyword));
            Assert.That(code.Keyword, Is.EqualTo(KeywordType.Abort));
            Assert.That(code.KeywordArgument, Is.EqualTo("foo bar"));
        }
    }

    [Test]
    public void ParseVar()
    {
        foreach (DuetAPI.Commands.Code code in Parse("  var asdf=0.34"))
        {
            Assert.That(code.Indent, Is.EqualTo(2));
            Assert.That(code.Type, Is.EqualTo(CodeType.Keyword));
            Assert.That(code.Keyword, Is.EqualTo(KeywordType.Var));
            Assert.That(code.KeywordArgument, Is.EqualTo("asdf=0.34"));
        }
    }

    [Test]
    public void ParseSet()
    {
        foreach (DuetAPI.Commands.Code code in Parse("  set asdf=\"meh\""))
        {
            Assert.That(code.Indent, Is.EqualTo(2));
            Assert.That(code.Type, Is.EqualTo(CodeType.Keyword));
            Assert.That(code.Keyword, Is.EqualTo(KeywordType.Set));
            Assert.That(code.KeywordArgument, Is.EqualTo("asdf=\"meh\""));
            Assert.That(code.Parameters.Count, Is.EqualTo(0));
        }
    }

    [Test]
    public void ParseGlobal()
    {
        foreach (DuetAPI.Commands.Code code in Parse(" \tglobal foo=\"bar\""))
        {
            Assert.That(code.Indent, Is.EqualTo(4));
            Assert.That(code.Type, Is.EqualTo(CodeType.Keyword));
            Assert.That(code.Keyword, Is.EqualTo(KeywordType.Global));
            Assert.That(code.KeywordArgument, Is.EqualTo("foo=\"bar\""));
        }
    }

    [Test]
    public void ParseEcho()
    {
        foreach (DuetAPI.Commands.Code code in Parse("echo {{3 + 3} + (volumes[0].freeSpace - 4)}"))
        {
            Assert.That(code.Indent, Is.EqualTo(0));
            Assert.That(code.Type, Is.EqualTo(CodeType.Keyword));
            Assert.That(code.Keyword, Is.EqualTo(KeywordType.Echo));
            Assert.That(code.KeywordArgument, Is.EqualTo("{{3 + 3} + (volumes[0].freeSpace - 4)}"));
        }
    }

    [Test]
    public void ParseEchoWithSemicolon()
    {
        foreach (DuetAPI.Commands.Code code in Parse("echo \"; this should work\""))
        {
            Assert.That(code.Indent, Is.EqualTo(0));
            Assert.That(code.Type, Is.EqualTo(CodeType.Keyword));
            Assert.That(code.Keyword, Is.EqualTo(KeywordType.Echo));
            Assert.That(code.KeywordArgument, Is.EqualTo("\"; this should work\""));
        }
    }

    [Test]
    public void ParseEchoWithBraces()
    {
        foreach (DuetAPI.Commands.Code code in Parse(" \techo \"debug \" ^ abs(3)"))
        {
            Assert.That(code.Indent, Is.EqualTo(4));
            Assert.That(code.Type, Is.EqualTo(CodeType.Keyword));
            Assert.That(code.Keyword, Is.EqualTo(KeywordType.Echo));
            Assert.That(code.KeywordArgument, Is.EqualTo("\"debug \" ^ abs(3)"));
        }
    }

    // DISABLED: SimpleCode now requires dependency injection
    /*
    [Test]
    public async Task ParseEchoWithQuote()
    {
        DuetControlServer.Commands.SimpleCode simpleCode = new() { Code = "echo \"M98 P\"\"revo/define-tool.g\"\" S\"" };
        List<DuetControlServer.Commands.Code> codes = [];
        await foreach (DuetControlServer.Commands.Code code in simpleCode.ParseAsync())
        {
            codes.Add(code);
        }

        Assert.That(codes.Count, Is.EqualTo(1));

        Assert.That(codes[0].Type, Is.EqualTo(CodeType.Keyword));
        Assert.That(codes[0].KeywordArgument, Is.EqualTo("\"M98 P\"\"revo/define-tool.g\"\" S\""));
    }
    */

    [Test]
    public void ParseEchoWithUnicode()
    {
        foreach (DuetAPI.Commands.Code code in Parse("echo \"💡 - LEDs on\""))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.Keyword));
            Assert.That(code.Keyword, Is.EqualTo(KeywordType.Echo));
            Assert.That(code.KeywordArgument, Is.EqualTo("\"💡 - LEDs on\""));
        }
    }

    [Test]
    public void ParseDynamicT()
    {
        foreach (DuetAPI.Commands.Code code in Parse("T{my.expression} P0"))
        {
            Assert.That(code.Flags, Is.EqualTo(CodeFlags.IsLastCode));
            Assert.That(code.Type, Is.EqualTo(CodeType.TCode));
            Assert.That(code.MajorNumber, Is.Null);
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Parameters.Count, Is.EqualTo(2));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('T'));
            Assert.That((string)code.Parameters[0], Is.EqualTo("{my.expression}"));
            Assert.That(code.Parameters[1].Letter, Is.EqualTo('P'));
            Assert.That((int)code.Parameters[1], Is.EqualTo(0));
        }
    }

    [Test]
    public void ParseNoSpaceComment()
    {
        foreach (DuetAPI.Commands.Code code in Parse("M84 XYE; disable motors"))
        {
            Assert.That(code.Flags, Is.EqualTo(CodeFlags.IsLastCode));
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(84));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Parameters.Count, Is.EqualTo(3));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('X'));
            Assert.That(code.Parameters[0].IsNull, Is.True);
            Assert.That(code.Parameters[1].Letter, Is.EqualTo('Y'));
            Assert.That(code.Parameters[1].IsNull, Is.True);
            Assert.That(code.Parameters[2].Letter, Is.EqualTo('E'));
            Assert.That(code.Parameters[2].IsNull, Is.True);
            Assert.That(code.Comment, Is.EqualTo(" disable motors"));
        }
    }

    [Test]
    public void ParseSpecialNumbers()
    {
        foreach (DuetAPI.Commands.Code code in Parse("M106 P0x123 S3"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(106));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Parameters.Count, Is.EqualTo(2));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('P'));
            Assert.That(code.Parameters[1].Letter, Is.EqualTo('S'));
            Assert.That((int)code.Parameters[0], Is.EqualTo(0x123));
            Assert.That((int)code.Parameters[1], Is.EqualTo(3));
            Assert.That(code.Comment, Is.Null);
        }

        foreach (DuetAPI.Commands.Code code in Parse("M106 P0 S3e2 ; foo"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(106));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Parameters.Count, Is.EqualTo(2));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('P'));
            Assert.That(code.Parameters[1].Letter, Is.EqualTo('S'));
            Assert.That((int)code.Parameters[0], Is.EqualTo(0));
            Assert.That((float)code.Parameters[1], Is.EqualTo(3e2));
            Assert.That(code.Comment, Is.EqualTo(" foo"));
        }

        foreach (DuetAPI.Commands.Code code in Parse("M106 P0 S3e-2 ; foobar"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(106));
            Assert.That(code.MinorNumber, Is.Null);
            Assert.That(code.Parameters.Count, Is.EqualTo(2));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('P'));
            Assert.That(code.Parameters[1].Letter, Is.EqualTo('S'));
            Assert.That((int)code.Parameters[0], Is.EqualTo(0));
            Assert.That((float)code.Parameters[1], Is.EqualTo(3e-2).Within(1e-3));
            Assert.That(code.Comment, Is.EqualTo(" foobar"));
        }
    }

    // DISABLED: SimpleCodes tests require DI for SimpleCode class
    /*
    [Test]
    public async Task SimpleCodes()
    {
        // See git history for implementation
    }

    [Test]
    public async Task SimpleCodesG53Line()
    {
        // See git history for implementation
    }

    [Test]
    public async Task SimpleCodesNL()
    {
        // See git history for implementation
    }

    [Test]
    public async Task SimpleCodesIndented()
    {
        // See git history for implementation
    }
    */

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
            Assert.That(code.Type, Is.EqualTo(CodeType.GCode));
            Assert.That(code.MajorNumber, Is.EqualTo(1));
            Assert.That(code.Flags, Is.EqualTo(CodeFlags.EnforceAbsolutePosition));
            Assert.That(code.LineNumber, Is.EqualTo(1));
            Assert.That(code.Parameters.Count, Is.EqualTo(3));
            Assert.That(code.GetInt('X'), Is.EqualTo(0));
            Assert.That(code.GetInt('Y'), Is.EqualTo(5));
            Assert.That(code.GetInt('F'), Is.EqualTo(3000));


            code.Reset();
            await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer);
            Assert.That(code.Type, Is.EqualTo(CodeType.GCode));
            Assert.That(code.MajorNumber, Is.EqualTo(0));
            Assert.That(code.Flags, Is.EqualTo(CodeFlags.EnforceAbsolutePosition | CodeFlags.IsLastCode));
            Assert.That(code.LineNumber, Is.EqualTo(1));
            Assert.That(code.Parameters.Count, Is.EqualTo(2));
            Assert.That(code.GetInt('X'), Is.EqualTo(5));
            Assert.That(code.GetInt('Y'), Is.EqualTo(10));
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

            Assert.That(code.Type, Is.EqualTo(CodeType.GCode));
            Assert.That(code.MajorNumber, Is.EqualTo(0));
            Assert.That(code.LineNumber, Is.EqualTo(3));
        }


        codeString = "G1 X1 Y5 F3000\nX5 F300\nY40";
        codeBytes = Encoding.UTF8.GetBytes(codeString);
        await using (MemoryStream memoryStream = new(codeBytes))
        {
            CodeParserBuffer buffer = new(128, true) { MayRepeatCode = true };
            DuetAPI.Commands.Code code = new() { LineNumber = 0 };

            await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer);
            Assert.That(code.LineNumber, Is.EqualTo(1));
            Assert.That(code.Type, Is.EqualTo(CodeType.GCode));
            Assert.That(code.MajorNumber, Is.EqualTo(1));
            Assert.That(code.Parameters.Count, Is.EqualTo(3));
            Assert.That(code.GetInt('X'), Is.EqualTo(1));
            Assert.That(code.GetInt('Y'), Is.EqualTo(5));
            Assert.That(code.GetInt('F'), Is.EqualTo(3000));

            code.Reset();
            await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer);
            Assert.That(code.Type, Is.EqualTo(CodeType.GCode));
            Assert.That(code.MajorNumber, Is.EqualTo(1));
            Assert.That(code.LineNumber, Is.EqualTo(2));
            Assert.That(code.Parameters.Count, Is.EqualTo(2));
            Assert.That(code.GetInt('X'), Is.EqualTo(5));
            Assert.That(code.GetInt('F'), Is.EqualTo(300));

            code.Reset();
            await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer);
            Assert.That(code.LineNumber, Is.EqualTo(3));
            Assert.That(code.Type, Is.EqualTo(CodeType.GCode));
            Assert.That(code.MajorNumber, Is.EqualTo(1));
            Assert.That(code.Parameters.Count, Is.EqualTo(1));
            Assert.That(code.GetInt('Y'), Is.EqualTo(40));
        }

        codeString = "G1 X1 Y5 F3000\n  G53 G1 X5 F300\n    G53 G0 Y40 G1 Z50\n  G4 S3\nG1 Z3";
        codeBytes = Encoding.UTF8.GetBytes(codeString);
        await using (MemoryStream memoryStream = new(codeBytes))
        {
            CodeParserBuffer buffer = new(128, true);

            DuetAPI.Commands.Code code = new() { LineNumber = 0 };
            await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer);
            Assert.That(code.Flags, Is.EqualTo(CodeFlags.IsLastCode));
            Assert.That(code.Indent, Is.EqualTo(0));
            Assert.That(code.LineNumber, Is.EqualTo(1));

            code.Reset();
            await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer);
            Assert.That(code.Flags, Is.EqualTo(CodeFlags.EnforceAbsolutePosition | CodeFlags.IsLastCode));
            Assert.That(code.Indent, Is.EqualTo(2));
            Assert.That(code.LineNumber, Is.EqualTo(2));

            code.Reset();
            await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer);
            Assert.That(code.Flags, Is.EqualTo(CodeFlags.EnforceAbsolutePosition));
            Assert.That(code.Indent, Is.EqualTo(4));
            Assert.That(code.LineNumber, Is.EqualTo(3));

            code.Reset();
            await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer);
            Assert.That(code.Flags, Is.EqualTo(CodeFlags.EnforceAbsolutePosition | CodeFlags.IsLastCode));
            Assert.That(code.Indent, Is.EqualTo(4));
            Assert.That(code.LineNumber, Is.EqualTo(3));

            code.Reset();
            await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer);
            Assert.That(code.Flags, Is.EqualTo(CodeFlags.IsLastCode));
            Assert.That(code.Indent, Is.EqualTo(2));
            Assert.That(code.LineNumber, Is.EqualTo(4));

            code.Reset();
            await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer);
            Assert.That(code.Flags, Is.EqualTo(CodeFlags.IsLastCode));
            Assert.That(code.Indent, Is.EqualTo(0));
            Assert.That(code.LineNumber, Is.EqualTo(5));
        }

        codeString = "M291 P\"Please go to <a href=\"\"https://www.duet3d.com/StartHere\"\" target=\"\"_blank\"\">this</a> page for further instructions on how to set it up.\" R\"Welcome to your new Duet 3!\" S1 T0";
        codeBytes = Encoding.UTF8.GetBytes(codeString);
        await using (MemoryStream memoryStream = new(codeBytes))
        {
            CodeParserBuffer buffer = new(128, true);

            DuetAPI.Commands.Code code = new();
            await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer);
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(291));
            Assert.That(code.GetString('P'), Is.EqualTo("Please go to <a href=\"https://www.duet3d.com/StartHere\" target=\"_blank\">this</a> page for further instructions on how to set it up."));
            Assert.That(code.GetString('R'), Is.EqualTo("Welcome to your new Duet 3!"));
            Assert.That(code.GetInt('S'), Is.EqualTo(1));
            Assert.That(code.GetInt('T'), Is.EqualTo(0));
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
