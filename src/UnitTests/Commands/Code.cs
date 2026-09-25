using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Utility;
using NUnit.Framework;

using System;
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
            Assert.That(code.Parameters.Count, Is.EqualTo(1));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('S'));
            Assert.That(code.GetInt('S', defaultValue: 0), Is.EqualTo(1));
        }
    }

    [Test]
    public void ParseG53()
    {
        foreach (DuetAPI.Commands.Code code in Parse("G53"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.GCode));
            Assert.That(code.MajorNumber, Is.EqualTo(53));
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));

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
            Assert.That(code.GetIntArray('E', steps.Length)!, Is.EqualTo(steps));
        }
    }

    [Test]
    public void ParseM98()
    {
        foreach (DuetAPI.Commands.Code code in Parse("M98 P\"config.g\""))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(98));
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
            Assert.That(code.Parameters.Count, Is.EqualTo(1));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('E'));
            Assert.That(code.Parameters[0].IsExpression, Is.True);
            Assert.That((string)code.Parameters[0], Is.EqualTo("{{123}:{456}:789}"));
        }

        foreach (DuetAPI.Commands.Code code in Parse("M584 E{123}:{456}:{789}"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(584));
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
            Assert.That(code.Parameters.Count, Is.EqualTo(1));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('E'));
            Assert.That(code.Parameters[0].IsExpression, Is.True);
            Assert.That((string)code.Parameters[0], Is.EqualTo("{123}:{456}:{789}"));
        }

        foreach (DuetAPI.Commands.Code code in Parse("M92 E{123,456}"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(92));
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
            Assert.That(code.Parameters.Count, Is.EqualTo(1));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('E'));
            Assert.That(code.Parameters[0].IsExpression, Is.True);
            Assert.That((string)code.Parameters[0], Is.EqualTo("{123,456}"));
        }

    }

    [Test]
    public void ParseM584WithoutDrivers()
    {
        // A letter given without a value names no driver. It must not read as driver 0.0, which is
        // what a driver ID parsed from an empty string defaults to
        foreach (DuetAPI.Commands.Code code in Parse("M584 X E2.0"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
            Assert.That(code.MajorNumber, Is.EqualTo(584));
            Assert.That(code.Parameters.Count, Is.EqualTo(2));
            Assert.That(code.Parameters[0].Letter, Is.EqualTo('X'));
            Assert.That(code.Parameters[0].IsNull, Is.True);
            Assert.That(code.Parameters[1].Letter, Is.EqualTo('E'));
            Assert.That((DriverId)code.Parameters[1], Is.EqualTo(new DriverId(2, 0)));
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
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
            Assert.That(code.MinorNumber, Is.EqualTo(-1));
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

    [Test]
    public void ParseSkip()
    {
        foreach (DuetAPI.Commands.Code code in Parse("skip M104 S205"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.Keyword));
            Assert.That(code.Keyword, Is.EqualTo(KeywordType.Skip));
            Assert.That(code.KeywordArgument, Is.Null);
            Assert.That(code.ToString(), Is.EqualTo("skip"));
        }
    }

    [Test]
    public void ParseMinorVersion()
    {
        // The minor version is a single fraction digit (0-9)
        foreach (DuetAPI.Commands.Code code in Parse("G28.1"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.GCode));
            Assert.That(code.MajorNumber, Is.EqualTo(28));
            Assert.That(code.MinorNumber, Is.EqualTo(1));
        }

        foreach (DuetAPI.Commands.Code code in Parse("G28.10"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.GCode));
            Assert.That(code.MajorNumber, Is.EqualTo(28));
            Assert.That(code.MinorNumber, Is.EqualTo(1));
        }
    }

    [Test]
    public void ParseUnicodeComment()
    {
        // Comments with non-ASCII characters must decode identically via the sync and async parsers
        foreach (DuetAPI.Commands.Code code in Parse("G1 X10 ; move to 10 mm - done 💡"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.GCode));
            Assert.That(code.MajorNumber, Is.EqualTo(1));
            Assert.That(code.Comment, Is.EqualTo(" move to 10 mm - done 💡"));
        }
    }

    [Test]
    public void ParseLength()
    {
        // The byte length must match between the sync and async parsers
        DuetAPI.Commands.Code[] codes = [.. Parse("G1 X10 Y20 ; some comment with Ümläute")];
        Assert.That(codes[1].Length, Is.EqualTo(codes[0].Length));
    }

    /// <summary>Read a value the way <c>GetFloat</c> and its siblings do</summary>
    private delegate T Getter<T, TRefusal>(char letter, T? defaultValue, T? min, T? max,
                                          Func<TRefusal, string>? errorString = null) where T : struct;

    /// <summary>Read a value into a plain out parameter, as the first Try shape does</summary>
    private delegate bool ValueTryGetter<T, TRefusal>(char letter, out T parameter, T? min, T? max,
                                                      Func<TRefusal, string>? errorString = null) where T : struct;

    /// <summary>Read a value into a nullable out parameter, as the second Try shape does</summary>
    private delegate bool NullableTryGetter<T, TRefusal>(char letter, out T? parameter, T? min, T? max,
                                                         Func<TRefusal, string>? errorString = null) where T : struct;

    /// <summary>Read an array the way <c>GetFloatArray</c> and its siblings do</summary>
    private delegate T[] ArrayGetter<T, TRefusal>(char letter, int maxLength, T[]? defaultValue, bool pad,
                                                  bool exactLength, bool allowZeroLength, T? min, T? max,
                                                  Func<TRefusal, string>? errorString = null) where T : struct;

    /// <summary>Read an array into an out parameter, as the array Try shape does</summary>
    private delegate bool ArrayTryGetter<T, TRefusal>(char letter, int maxLength, out T[]? parameter, bool pad,
                                                      bool exactLength, bool allowZeroLength, T? min, T? max,
                                                      Func<TRefusal, string>? errorString = null) where T : struct;

    /// <summary>
    /// Hold one numeric type's three accessors to every parameter each of them takes
    /// </summary>
    /// <param name="what">Name of the type, to tell the assertions of one type from another's</param>
    /// <param name="get">The returning accessor</param>
    /// <param name="tryGetValue">The Try accessor that writes a plain out parameter</param>
    /// <param name="tryGetNullable">The Try accessor that writes a nullable out parameter</param>
    /// <param name="present">Letter the code carries</param>
    /// <param name="absent">Letter the code does not carry</param>
    /// <param name="value">What the code carries at <paramref name="present" /></param>
    /// <param name="below">A value lower than <paramref name="value" /></param>
    /// <param name="above">A value higher than <paramref name="value" /></param>
    /// <param name="fallback">A default to hand the accessors, unequal to every other value here</param>
    /// <param name="bare">Letter the code carries with nothing after it</param>
    /// <remarks>
    /// The limits throw where the value stands rather than storing the nearest one that fits, as
    /// RepRapFirmware's GCodeBuffer::GetLimitedFValue, ::GetLimitedIValue and ::GetLimitedUIValue do,
    /// because the nearest one that fits is not what the line asked for.
    /// <paramref name="bare" /> is held to the third answer these accessors have to give: a letter
    /// written with nothing after it is a parse error rather than a missing parameter, and
    /// RepRapFirmware answers it the same way whichever accessor read it, Seen followed by GetFValue
    /// reaching the parser's "expected number". The defaulting and Try forms used to answer it as a
    /// failed conversion instead, which named a CLR type in a reply an operator reads
    /// </remarks>
    private static void AssertNumericAccessors<T, TRefusal>(string what, Getter<T, TRefusal> get,
                                                            ValueTryGetter<T, TRefusal> tryGetValue,
                                                            NullableTryGetter<T, TRefusal> tryGetNullable,
                                                            char present, char absent,
                                                  T value, T below, T above, T fallback, char bare) where T : struct
    {
        string tooLow = $"parameter '{present}' too low", tooHigh = $"parameter '{present}' too high";
        string noValue = $"expected number after '{bare}'";

        Assert.Multiple(() =>
        {
            // defaultValue
            Assert.That(get(absent, fallback, null, null), Is.EqualTo(fallback), $"{what}: the default answers an absent letter");
            Assert.That(get(present, fallback, null, null), Is.EqualTo(value), $"{what}: and stands aside for a letter that is there");
            Assert.That(get(absent, fallback, above, above), Is.EqualTo(fallback), $"{what}: the limits are about the line, so they do not reach the default");
            Assert.That(Assert.Throws<MissingParameterException>(() => get(absent, null, null, null))!.Letter,
                        Is.EqualTo(absent), $"{what}: with no default the letter has to be there");

            // min
            Assert.That(get(present, null, below, null), Is.EqualTo(value), $"{what}: a value above min is taken");
            Assert.That(get(present, null, value, null), Is.EqualTo(value), $"{what}: min is inclusive");
            Assert.That(Assert.Throws<GCodeException>(() => get(present, null, above, null))!.Message,
                        Is.EqualTo(tooLow), $"{what}: and a value beneath it is refused");

            // max
            Assert.That(get(present, null, null, above), Is.EqualTo(value), $"{what}: a value below max is taken");
            Assert.That(get(present, null, null, value), Is.EqualTo(value), $"{what}: max is inclusive");
            Assert.That(Assert.Throws<GCodeException>(() => get(present, null, null, below))!.Message,
                        Is.EqualTo(tooHigh), $"{what}: and a value above it is refused");

            // all three together
            Assert.That(get(present, fallback, below, above), Is.EqualTo(value), $"{what}: the line wins between both ends");

            // the Try shape that writes a plain out parameter
            Assert.That(tryGetValue(present, out T found, below, above), Is.True, $"{what}: Try finds the letter");
            Assert.That(found, Is.EqualTo(value), $"{what}: and hands back what it read");
            Assert.That(tryGetValue(present, out T atEnds, value, value), Is.True, $"{what}: Try holds both ends inclusive");
            Assert.That(atEnds, Is.EqualTo(value));
            Assert.That(tryGetValue(absent, out T missed, above, below), Is.False, $"{what}: an absent letter is neither found nor limit-checked");
            Assert.That(missed, Is.EqualTo(default(T)), $"{what}: and leaves the type's own default behind");
            Assert.That(Assert.Throws<GCodeException>(() => tryGetValue(present, out T _, above, null))!.Message,
                        Is.EqualTo(tooLow), $"{what}: Try refuses beneath min");
            Assert.That(Assert.Throws<GCodeException>(() => tryGetValue(present, out T _, null, below))!.Message,
                        Is.EqualTo(tooHigh), $"{what}: Try refuses above max");

            // the Try shape that writes a nullable out parameter
            Assert.That(tryGetNullable(present, out T? foundOrNull, below, above), Is.True, $"{what}: the nullable Try finds the letter");
            Assert.That(foundOrNull, Is.EqualTo(value), $"{what}: and hands back what it read");
            Assert.That(tryGetNullable(present, out T? atEndsOrNull, value, value), Is.True, $"{what}: it holds both ends inclusive too");
            Assert.That(atEndsOrNull, Is.EqualTo(value));
            Assert.That(tryGetNullable(absent, out T? missedOrNull, above, below), Is.False, $"{what}: an absent letter is neither found nor limit-checked");
            Assert.That(missedOrNull, Is.Null, $"{what}: and leaves null behind");
            Assert.That(Assert.Throws<GCodeException>(() => tryGetNullable(present, out T? _, above, null))!.Message,
                        Is.EqualTo(tooLow), $"{what}: the nullable Try refuses beneath min");
            Assert.That(Assert.Throws<GCodeException>(() => tryGetNullable(present, out T? _, null, below))!.Message,
                        Is.EqualTo(tooHigh), $"{what}: the nullable Try refuses above max");

            // a letter written with nothing after it, which none of the three may take for absent
            Assert.That(Assert.Throws<GCodeException>(() => get(bare, null, null, null))!.Message,
                        Is.EqualTo(noValue), $"{what}: a letter with no value is a parse error");
            Assert.That(Assert.Throws<GCodeException>(() => get(bare, fallback, null, null))!.Message,
                        Is.EqualTo(noValue), $"{what}: a default stands in for an absent letter, not for one written badly");
            Assert.That(Assert.Throws<GCodeException>(() => tryGetValue(bare, out T _, null, null))!.Message,
                        Is.EqualTo(noValue), $"{what}: Try refuses it too");
            Assert.That(Assert.Throws<GCodeException>(() => tryGetNullable(bare, out T? _, null, null))!.Message,
                        Is.EqualTo(noValue), $"{what}: and so does the nullable Try");
        });
    }

    /// <summary>
    /// Hold one array type's two accessors to every parameter each of them takes
    /// </summary>
    /// <param name="what">Name of the type, to tell the assertions of one type from another's</param>
    /// <param name="get">The returning accessor</param>
    /// <param name="tryGet">The Try accessor</param>
    /// <param name="present">Letter the code carries</param>
    /// <param name="absent">Letter the code does not carry</param>
    /// <param name="values">What the code carries at <paramref name="present" />, in ascending order</param>
    /// <param name="below">A value lower than every item</param>
    /// <param name="above">A value higher than every item</param>
    /// <param name="fallback">A default to hand the accessors, unequal to <paramref name="values" /></param>
    /// <param name="bare">Letter the code carries with nothing after it</param>
    /// <remarks>
    /// The limits are held against the middle item as well as the ends, so a check that only looked
    /// at the first item or only at the last would fail here. <paramref name="bare" /> is held to the
    /// same refusal the scalar accessors give, which the array accessors used to disagree over
    /// </remarks>
    private static void AssertArrayAccessors<T, TRefusal>(string what, ArrayGetter<T, TRefusal> get,
                                                          ArrayTryGetter<T, TRefusal> tryGet,
                                                char present, char absent, T[] values, T below, T above,
                                                T[] fallback, char bare) where T : struct
    {
        string tooLow = $"parameter '{present}' too low", tooHigh = $"parameter '{present}' too high";
        string noValue = $"expected number after '{bare}'";
        T first = values[0], middle = values[1], last = values[^1];
        int room = values.Length;

        Assert.Multiple(() =>
        {
            // defaultValue
            Assert.That(get(absent, room, fallback, false, false, false, null, null), Is.EqualTo(fallback), $"{what}: the default answers an absent letter");
            Assert.That(get(present, room, fallback, false, false, false, null, null), Is.EqualTo(values), $"{what}: and stands aside for a letter that is there");
            Assert.That(get(absent, room, fallback, false, false, false, above, below), Is.EqualTo(fallback), $"{what}: the limits are about the line, so they do not reach the default");
            Assert.That(Assert.Throws<MissingParameterException>(() => get(absent, room, null, false, false, false, null, null))!.Letter,
                        Is.EqualTo(absent), $"{what}: with no default the letter has to be there");

            // min, against every item
            Assert.That(get(present, room, null, false, false, false, below, null), Is.EqualTo(values), $"{what}: every item above min is taken");
            Assert.That(get(present, room, null, false, false, false, first, null), Is.EqualTo(values), $"{what}: min is inclusive at the lowest item");
            Assert.That(Assert.Throws<GCodeException>(() => get(present, room, null, false, false, false, middle, null))!.Message,
                        Is.EqualTo(tooLow), $"{what}: an item beneath min refuses the list, first item or not");

            // max, against every item
            Assert.That(get(present, room, null, false, false, false, null, above), Is.EqualTo(values), $"{what}: every item below max is taken");
            Assert.That(get(present, room, null, false, false, false, null, last), Is.EqualTo(values), $"{what}: max is inclusive at the highest item");
            Assert.That(Assert.Throws<GCodeException>(() => get(present, room, null, false, false, false, null, middle))!.Message,
                        Is.EqualTo(tooHigh), $"{what}: an item above max refuses the list, last item or not");

            // all three together
            Assert.That(get(present, room, fallback, false, false, false, below, above), Is.EqualTo(values), $"{what}: the line wins between both ends");

            // the Try shape
            Assert.That(tryGet(present, room, out T[]? found, false, false, false, below, above), Is.True, $"{what}: Try finds the letter");
            Assert.That(found, Is.EqualTo(values), $"{what}: and hands back what it read");
            Assert.That(tryGet(present, room, out T[]? atEnds, false, false, false, first, last), Is.True, $"{what}: Try holds both ends inclusive");
            Assert.That(atEnds, Is.EqualTo(values));
            Assert.That(tryGet(absent, room, out T[]? missed, false, false, false, above, below), Is.False, $"{what}: an absent letter is neither found nor limit-checked");
            Assert.That(missed, Is.Null, $"{what}: and leaves null behind");
            Assert.That(Assert.Throws<GCodeException>(() => tryGet(present, room, out T[]? _, false, false, false, middle, null))!.Message,
                        Is.EqualTo(tooLow), $"{what}: Try refuses an item beneath min");
            Assert.That(Assert.Throws<GCodeException>(() => tryGet(present, room, out T[]? _, false, false, false, null, middle))!.Message,
                        Is.EqualTo(tooHigh), $"{what}: Try refuses an item above max");

            // a letter written with nothing after it, which neither accessor may take for absent
            Assert.That(Assert.Throws<GCodeException>(() => get(bare, room, null, false, false, false, null, null))!.Message,
                        Is.EqualTo(noValue), $"{what}: a letter with no value is a parse error");
            Assert.That(Assert.Throws<GCodeException>(() => get(bare, room, fallback, false, false, false, null, null))!.Message,
                        Is.EqualTo(noValue), $"{what}: a default does not stand in for a list written badly");
            Assert.That(Assert.Throws<GCodeException>(() => tryGet(bare, room, out T[]? _, false, false, false, null, null))!.Message,
                        Is.EqualTo(noValue), $"{what}: Try refuses it too");
        });
    }

    [Test]
    public void GetAndTryGetFloatCoverDefaultMinAndMax()
    {
        DuetAPI.Commands.Code code = new("M906 X50 K");
        AssertNumericAccessors<float, float>("float", code.GetFloat, code.TryGetFloat, code.TryGetFloat,
                                      present: 'X', absent: 'Z', value: 50.0f, below: 10.0f, above: 100.0f,
                                      fallback: 7.5f, bare: 'K');
    }

    [Test]
    public void GetAndTryGetIntCoverDefaultMinAndMax()
    {
        DuetAPI.Commands.Code code = new("M906 X50 K");
        AssertNumericAccessors<int, long>("int", code.GetInt, code.TryGetInt, code.TryGetInt,
                                    present: 'X', absent: 'Z', value: 50, below: 10, above: 100, fallback: -7, bare: 'K');
    }

    [Test]
    public void GetAndTryGetUIntCoverDefaultMinAndMax()
    {
        DuetAPI.Commands.Code code = new("M906 X50 K");
        AssertNumericAccessors<uint, long>("uint", code.GetUInt, code.TryGetUInt, code.TryGetUInt,
                                     present: 'X', absent: 'Z', value: 50u, below: 10u, above: 100u, fallback: 7u, bare: 'K');
    }

    [Test]
    public void GetAndTryGetLongCoverDefaultMinAndMax()
    {
        DuetAPI.Commands.Code code = new("M906 X50 K");
        AssertNumericAccessors<long, long>("long", code.GetLong, code.TryGetLong, code.TryGetLong,
                                     present: 'X', absent: 'Z', value: 50L, below: 10L, above: 100L, fallback: -7L, bare: 'K');
    }

    [Test]
    public void GetAndTryGetFloatArrayCoverDefaultMinAndMax()
    {
        DuetAPI.Commands.Code code = new("M906 E10:50:100 K");
        AssertArrayAccessors<float, float>("float[]", code.GetFloatArray, code.TryGetFloatArray,
                                    present: 'E', absent: 'Z', values: [10.0f, 50.0f, 100.0f],
                                    below: 0.0f, above: 200.0f, fallback: [7.5f], bare: 'K');
    }

    [Test]
    public void GetAndTryGetIntArrayCoverDefaultMinAndMax()
    {
        DuetAPI.Commands.Code code = new("M906 E10:50:100 K");
        AssertArrayAccessors<int, long>("int[]", code.GetIntArray, code.TryGetIntArray,
                                  present: 'E', absent: 'Z', values: [10, 50, 100],
                                  below: 0, above: 200, fallback: [-7], bare: 'K');
    }

    [Test]
    public void GetAndTryGetUIntArrayCoverDefaultMinAndMax()
    {
        DuetAPI.Commands.Code code = new("M906 E10:50:100 K");
        AssertArrayAccessors<uint, long>("uint[]", code.GetUIntArray, code.TryGetUIntArray,
                                   present: 'E', absent: 'Z', values: [10u, 50u, 100u],
                                   below: 0u, above: 200u, fallback: [7u], bare: 'K');
    }

    [Test]
    public void GetAndTryGetLongArrayCoverDefaultMinAndMax()
    {
        DuetAPI.Commands.Code code = new("M906 E10:50:100 K");
        AssertArrayAccessors<long, long>("long[]", code.GetLongArray, code.TryGetLongArray,
                                   present: 'E', absent: 'Z', values: [10L, 50L, 100L],
                                   below: 0L, above: 200L, fallback: [-7L], bare: 'K');
    }

    [Test]
    public void GetBoolCoversItsDefaultAndItsTryShapesTakeNoLimits()
    {
        // GetBool is the one numeric accessor with no min and no max, because the two values a
        // boolean can hold are both inside any range that would admit either of them
        DuetAPI.Commands.Code code = new("M42 S1 K");

        Assert.Multiple(() =>
        {
            Assert.That(code.GetBool('S', defaultValue: false), Is.True, "a letter that is there wins over the default");
            Assert.That(code.GetBool('Z', defaultValue: true), Is.True, "and the default answers an absent one");
            Assert.That(code.GetBool('Z', defaultValue: false), Is.False);
            Assert.That(Assert.Throws<MissingParameterException>(() => code.GetBool('Z'))!.Letter,
                        Is.EqualTo('Z'), "with no default the letter has to be there");

            Assert.That(code.TryGetBool('S', out bool found), Is.True);
            Assert.That(found, Is.True);
            Assert.That(code.TryGetBool('Z', out bool missed), Is.False);
            Assert.That(missed, Is.False);

            Assert.That(code.TryGetBool('S', out bool? foundOrNull), Is.True);
            Assert.That(foundOrNull, Is.True);
            Assert.That(code.TryGetBool('Z', out bool? missedOrNull), Is.False);
            Assert.That(missedOrNull, Is.Null);

            // and a letter with no value is the parse error it is for every other accessor
            Assert.That(Assert.Throws<GCodeException>(() => code.GetBool('K'))!.Message,
                        Is.EqualTo("expected number after 'K'"));
            Assert.That(Assert.Throws<GCodeException>(() => code.GetBool('K', defaultValue: true))!.Message,
                        Is.EqualTo("expected number after 'K'"));
            Assert.That(Assert.Throws<GCodeException>(() => code.TryGetBool('K', out bool _))!.Message,
                        Is.EqualTo("expected number after 'K'"));
            Assert.That(Assert.Throws<GCodeException>(() => code.TryGetBool('K', out bool? _))!.Message,
                        Is.EqualTo("expected number after 'K'"));
        });
    }

    [Test]
    public void TheAccessorsThatTakeOnlyADefaultCoverIt()
    {
        // A string, a driver ID and an IP address have no magnitude to hold against a range, so the
        // default is the only parameter they take besides the letter
        DuetAPI.Commands.Code code = new("M569 P1.2 S\"named\" I192.168.1.5 K");
        DriverId fallbackId = new("9.9");
        DriverId[] fallbackIds = [new("8.8")];
        IPAddress fallbackAddress = IPAddress.Loopback;

        Assert.Multiple(() =>
        {
            Assert.That(code.GetString('S', defaultValue: "other"), Is.EqualTo("named"));
            Assert.That(code.GetString('Z', defaultValue: "other"), Is.EqualTo("other"));
            Assert.That(Assert.Throws<MissingParameterException>(() => code.GetString('Z'))!.Letter, Is.EqualTo('Z'));

            Assert.That(code.GetDriverId('P', defaultValue: fallbackId), Is.EqualTo(new DriverId(1, 2)));
            Assert.That(code.GetDriverId('Z', defaultValue: fallbackId), Is.EqualTo(fallbackId));
            Assert.That(Assert.Throws<MissingParameterException>(() => code.GetDriverId('Z'))!.Letter, Is.EqualTo('Z'));

            Assert.That(code.GetIPAddress('I', defaultValue: fallbackAddress), Is.EqualTo(IPAddress.Parse("192.168.1.5")));
            Assert.That(code.GetIPAddress('Z', defaultValue: fallbackAddress), Is.EqualTo(fallbackAddress));
            Assert.That(Assert.Throws<MissingParameterException>(() => code.GetIPAddress('Z'))!.Letter, Is.EqualTo('Z'));

            Assert.That(code.GetDriverIdArray('P', 4, defaultValue: fallbackIds), Is.EqualTo(new DriverId[] { new(1, 2) }));
            Assert.That(code.GetDriverIdArray('Z', 4, defaultValue: fallbackIds), Is.EqualTo(fallbackIds));
            Assert.That(Assert.Throws<MissingParameterException>(() => code.GetDriverIdArray('Z', 4))!.Letter, Is.EqualTo('Z'));

            Assert.That(code.TryGetDriverIdArray('P', 4, out DriverId[]? found), Is.True);
            Assert.That(found, Is.EqualTo(new DriverId[] { new(1, 2) }));
            Assert.That(code.TryGetDriverIdArray('Z', 4, out DriverId[]? missed), Is.False);
            Assert.That(missed, Is.Null);

            // and a letter with no value is the parse error it is for every other accessor
            Assert.That(Assert.Throws<GCodeException>(() => code.GetDriverIdArray('K', 4))!.Message,
                        Is.EqualTo("expected number after 'K'"));
            Assert.That(Assert.Throws<GCodeException>(() => code.GetDriverIdArray('K', 4, defaultValue: fallbackIds))!.Message,
                        Is.EqualTo("expected number after 'K'"));
            Assert.That(Assert.Throws<GCodeException>(() => code.TryGetDriverIdArray('K', 4, out DriverId[]? _))!.Message,
                        Is.EqualTo("expected number after 'K'"));
        });
    }

    [Test]
    public void RefusalsQuoteTheColumnTheValueStoodIn()
    {
        // A value refused for what it is belongs to the place in the line it stood, whatever type
        // went looking for it, so every limited read quotes the column. RepRapFirmware quotes one
        // from GetLimitedFValue (GCodeBuffer.cpp:553) and none from GetLimitedIValue or
        // ::GetLimitedUIValue (:592 and :614), having only there a read that knows one. A letter
        // with no value is the other refusal that carries a column
        DuetAPI.Commands.Code code = new("M906 I120 S12 E100:2000:50 X1:2:3 K");

        Assert.Multiple(() =>
        {
            Assert.That(Assert.Throws<GCodeException>(() => code.GetFloat('I', max: 100.0f))!.Column,
                        Is.EqualTo(6), "where the value stood, counting from zero");
            Assert.That(Assert.Throws<GCodeException>(() => code.GetFloat('I', min: 200.0f))!.Column,
                        Is.EqualTo(6), "and the same for a value beneath its floor");
            Assert.That(Assert.Throws<GCodeException>(() => code.GetUInt('S', max: 10))!.Column,
                        Is.EqualTo(11), "an integer read quotes it as a float read does");
            Assert.That(Assert.Throws<GCodeException>(() => code.GetUInt('S', min: 20))!.Column,
                        Is.EqualTo(11), "at either end");

            Assert.That(Assert.Throws<GCodeException>(() => code.GetFloatArray('E', 3, max: 1000.0f))!.Column,
                        Is.EqualTo(15), "an array quotes where its list began");
            Assert.That(Assert.Throws<GCodeException>(() => code.GetFloatArray('E', 3, min: 60.0f))!.Column,
                        Is.EqualTo(15), "at either end");
            Assert.That(Assert.Throws<GCodeException>(() => code.GetIntArray('X', 3, max: 2))!.Column,
                        Is.EqualTo(28), "and an integer list where its own began");
            Assert.That(Assert.Throws<GCodeException>(() => code.GetIntArray('X', 3, min: 2))!.Column,
                        Is.EqualTo(28), "at either end");

            Assert.That(Assert.Throws<GCodeException>(() => code.GetFloat('K'))!.Column, Is.EqualTo(35),
                        "a letter with no value is quoted by where its value should have begun");
            Assert.That(Assert.Throws<GCodeException>(() => code.GetInt('K'))!.Column, Is.EqualTo(35),
                        "whatever type went looking for it, the refusal coming from the letter rather than the value");
        });
    }

    [Test]
    public void AnArrayIsHeldToTheLengthItIsReadUnder()
    {
        // The line the regression suite records M906 against, on a machine with three extruders:
        // RepRapFirmware refuses the fourth value where it stands rather than setting the first
        // three, because the code asked for something the machine cannot do
        DuetAPI.Commands.Code code = new("M906 E400:410:420:430");

        Assert.Multiple(() =>
        {
            GCodeException refused = Assert.Throws<GCodeException>(() => code.GetFloatArray('E', 3))!;
            Assert.That(refused.Message, Is.EqualTo("array too long for parameter 'E'"),
                        "as StringParser::CheckArrayLength words it");
            Assert.That(refused.Column, Is.EqualTo(18),
                        "where the first value past the limit begins, which the reply quotes as column 19");

            Assert.That(code.GetFloatArray('E', 4), Is.EqualTo(new[] { 400.0f, 410.0f, 420.0f, 430.0f }),
                        "a list the caller can take is taken whole");
            Assert.That(code.GetFloatArray('E', 5), Is.EqualTo(new[] { 400.0f, 410.0f, 420.0f, 430.0f }),
                        "and a shorter one leaves the rest to the caller");

            Assert.That(Assert.Throws<GCodeException>(() => code.TryGetFloatArray('E', 3, out float[]? _))!.Message,
                        Is.EqualTo("array too long for parameter 'E'"), "the Try shape refuses it too");
        });
    }

    [Test]
    public void AnArrayRefusesEveryValueBeyondTheFirstItCannotTake()
    {
        // The column is the one that value stands in, so a list refused at its second item does not
        // quote where the list began
        DuetAPI.Commands.Code code = new("M92 X80 E420:420:420");

        Assert.Multiple(() =>
        {
            Assert.That(Assert.Throws<GCodeException>(() => code.GetFloatArray('E', 1))!.Column, Is.EqualTo(13));
            Assert.That(Assert.Throws<GCodeException>(() => code.GetFloatArray('E', 2))!.Column, Is.EqualTo(17));
            Assert.That(Assert.Throws<GCodeException>(() => code.GetFloatArray('X', 0))!.Column, Is.EqualTo(5),
                        "a caller that can take nothing refuses the letter where its value begins");
        });
    }

    [Test]
    public void ACallerThatCanTakeNothingRefusesTheLetterBeforeItsValue()
    {
        // M17 E on a machine with no extruders, which the regression suite records as "array too
        // long for parameter 'E'" rather than as a letter with no number after it: RepRapFirmware
        // checks the length before it reads the value
        DuetAPI.Commands.Code code = new("M17 E");

        Assert.Multiple(() =>
        {
            GCodeException refused = Assert.Throws<GCodeException>(() => code.GetIntArray('E', 0))!;
            Assert.That(refused.Message, Is.EqualTo("array too long for parameter 'E'"));
            Assert.That(refused.Column, Is.EqualTo(5), "quoted as column 6");

            Assert.That(Assert.Throws<GCodeException>(() => code.GetIntArray('E', 1))!.Message,
                        Is.EqualTo("expected number after 'E'"),
                        "with room for a value, the letter without one is the parse error it always was");
        });
    }

    [Test]
    public void OneValueStandsForEveryOneOfThemWhenTheCallerPads()
    {
        // M906 E800 on a machine with three extruders sets all three, which is how nearly every
        // configuration is written (GCodeBuffer::GetFloatArray with doPad)
        DuetAPI.Commands.Code code = new("M906 E800 X400:500");

        Assert.Multiple(() =>
        {
            Assert.That(code.GetFloatArray('E', 3, pad: true), Is.EqualTo(new[] { 800.0f, 800.0f, 800.0f }));
            Assert.That(code.GetFloatArray('E', 3), Is.EqualTo(new[] { 800.0f }),
                        "without padding the one value stays one value");
            Assert.That(code.GetFloatArray('E', 1, pad: true), Is.EqualTo(new[] { 800.0f }),
                        "and there is nothing to pad when the caller takes one");
            Assert.That(code.GetFloatArray('X', 3, pad: true), Is.EqualTo(new[] { 400.0f, 500.0f }),
                        "a list of more than one is taken as it stands, because only one value can stand for all");
        });
    }

    [Test]
    public void AnExactLengthRefusesAListThatStopsShort()
    {
        // M557 describes each axis with a minimum and a maximum, so anything else is refused
        // (GCodeBuffer::TryGetFloatArray, which checks the count it got back)
        DuetAPI.Commands.Code code = new("M557 X20 Y20:180 Z0:10:20");

        Assert.Multiple(() =>
        {
            Assert.That(code.GetFloatArray('Y', 2, exactLength: true), Is.EqualTo(new[] { 20.0f, 180.0f }));
            Assert.That(Assert.Throws<GCodeException>(() => code.GetFloatArray('X', 2, exactLength: true))!.Message,
                        Is.EqualTo("Wrong number of values in array, expected 2"));
            Assert.That(Assert.Throws<GCodeException>(() => code.GetFloatArray('Z', 2, exactLength: true))!.Message,
                        Is.EqualTo("array too long for parameter 'Z'"),
                        "a list too long is refused as too long, because the length is checked as it is read");
            Assert.That(code.GetFloatArray('X', 2, pad: true, exactLength: true), Is.EqualTo(new[] { 20.0f, 20.0f }),
                        "padding happens first, so one value satisfies a caller that pads");
        });
    }

    [Test]
    public void AnArrayFromNoLineHasNoColumnToRefuseAt()
    {
        // A parameter built by a caller or left by an evaluated expression never stood anywhere, so
        // the refusal names the parameter and quotes nothing
        DuetAPI.Commands.Code code = new();
        code.Parameters.Add(new CodeParameter('E', new float[] { 1.0f, 2.0f, 3.0f }));

        GCodeException refused = Assert.Throws<GCodeException>(() => code.GetFloatArray('E', 2))!;
        Assert.Multiple(() =>
        {
            Assert.That(refused.Message, Is.EqualTo("array too long for parameter 'E'"));
            Assert.That(refused.Column, Is.EqualTo(CodeParameter.NoColumn));
        });
    }

    [Test]
    public void AnAbsentParameterIsNotFound()
    {
        // A letter the line does not carry has to read as absent rather than as a parameter holding
        // zero, whichever of the lookups goes after it
        DuetAPI.Commands.Code code = new("M950 F0 C\"1.out3\" K4");

        Assert.Multiple(() =>
        {
            Assert.That(code.HasParameter('Z'), Is.False);
            Assert.That(code.TryGetParameter('Z', out CodeParameter? absent), Is.False);
            Assert.That(absent, Is.Null);

            Assert.That(code.TryGetParameter('F', out CodeParameter? present), Is.True,
                        "a letter that is there still comes back");
            Assert.That((int)present!, Is.EqualTo(0),
                        "including one whose value is zero, which is not the same as not being there");
        });
    }

    [Test]
    public void GetParameterWithADefaultBuildsOneThatWasNotInTheLine()
    {
        // The default overload stands in for the missing letter so a caller can read it like any
        // other parameter. It never came from the line, so it has no column to quote
        DuetAPI.Commands.Code code = new("M950 F0");

        CodeParameter standIn = code.GetParameter('Q', defaultValue: 500);
        Assert.Multiple(() =>
        {
            Assert.That(standIn.Letter, Is.EqualTo('Q'));
            Assert.That((int)standIn, Is.EqualTo(500));
            Assert.That(standIn.Column, Is.EqualTo(CodeParameter.NoColumn));
            Assert.That(code.GetParameter('F', defaultValue: 9), Is.SameAs(code.GetParameter('F', defaultValue: 0)),
                        "a letter that is there is returned as it stands, not replaced by the default");
        });
    }

    [Test]
    public void GetParameterWithoutADefaultRefusesALetterThatIsNotInTheLine()
    {
        // No default is the caller saying it has nothing to fall back on, so the miss is refused
        // where it is looked up instead of coming back as a parameter the caller has to check
        DuetAPI.Commands.Code code = new("M950 F0");

        Assert.Multiple(() =>
        {
            Assert.That(Assert.Throws<MissingParameterException>(() => code.GetParameter('Q'))!.Letter,
                        Is.EqualTo('Q'));
            Assert.That((int)code.GetParameter('F'), Is.EqualTo(0),
                        "a letter that is there is still returned as it stands");
        });
    }

    [Test]
    public void TryGetOverwritesReferenceTypesWithNullWhenTheParameterIsAbsent()
    {
        // A string, a driver ID, an IP address and every array answer the miss with null, which is
        // what tells a caller that reads the value without checking the result apart from one the
        // code really supplied. The nullable numeric forms are held to the same rule by the matrix
        DuetAPI.Commands.Code code = new("M950 F0 C\"1.out3\" K4");

        string? stringValue = "held";
        DriverId? driverId = new("1.2");
        IPAddress? ipAddress = IPAddress.Loopback;
        float[]? floatArray = [1.0f];
        int[]? intArray = [1];
        uint[]? uintArray = [1u];
        long[]? longArray = [1L];
        DriverId[]? driverIdArray = [new("1.2")];

        Assert.Multiple(() =>
        {
            Assert.That(code.TryGetString('Z', out stringValue), Is.False);
            Assert.That(code.TryGetDriverId('Z', out driverId), Is.False);
            Assert.That(code.TryGetIPAddress('Z', out ipAddress), Is.False);
            Assert.That(code.TryGetFloatArray('Z', 2, out floatArray), Is.False);
            Assert.That(code.TryGetIntArray('Z', 2, out intArray), Is.False);
            Assert.That(code.TryGetUIntArray('Z', 2, out uintArray), Is.False);
            Assert.That(code.TryGetLongArray('Z', 2, out longArray), Is.False);
            Assert.That(code.TryGetDriverIdArray('Z', 2, out driverIdArray), Is.False);
        });

        Assert.Multiple(() =>
        {
            Assert.That(stringValue, Is.Null);
            Assert.That(driverId, Is.Null);
            Assert.That(ipAddress, Is.Null);
            Assert.That(floatArray, Is.Null);
            Assert.That(intArray, Is.Null);
            Assert.That(uintArray, Is.Null);
            Assert.That(longArray, Is.Null);
            Assert.That(driverIdArray, Is.Null);
        });
    }

    /// <summary>
    /// A contiguous enumeration, as every one a G-code parameter selects from is
    /// </summary>
    private enum Shade
    {
        Off = 0,
        Dim = 1,
        Bright = 2
    }

    /// <summary>
    /// An enumeration that does not start at zero, to hold the refusal to naming the right end
    /// </summary>
    private enum Offset
    {
        Below = -1,
        Level = 0,
        Above = 1
    }

    /// <summary>
    /// GetEnum and TryGetEnum read a parameter as one of an enumeration's members, and refuse a
    /// value that names none of them the way every other limited read refuses one
    /// </summary>
    /// <remarks>
    /// The enumeration is the limits, so the wording is RepRapFirmware's for
    /// <c>GCodeBuffer::GetLimitedUIValue</c>: "too low" below the lowest member and "too high" above
    /// the highest. A default is returned as it stands, as it is for the numeric accessors, because
    /// it comes from the caller rather than from the line
    /// </remarks>
    [Test]
    public void EnumAccessors()
    {
        foreach (DuetAPI.Commands.Code code in Parse("M1 S1 T3 U-2 V"))
        {
            Assert.Multiple(() =>
            {
                Assert.That(code.GetEnum<Shade>('S'), Is.EqualTo(Shade.Dim), "a value names its member");
                Assert.That(code.GetEnum<Shade>('Q', Shade.Bright), Is.EqualTo(Shade.Bright),
                            "the default answers an absent letter");
                Assert.That(code.GetEnum<Shade>('S', Shade.Bright), Is.EqualTo(Shade.Dim),
                            "and stands aside for a letter that is there");
                Assert.That(Assert.Throws<MissingParameterException>(() => code.GetEnum<Shade>('Q'))!.Letter,
                            Is.EqualTo('Q'), "with no default the letter has to be there");

                Assert.That(Assert.Throws<GCodeException>(() => code.GetEnum<Shade>('T'))!.Message,
                            Is.EqualTo("parameter 'T' too high"), "a value above every member is too high");
                Assert.That(Assert.Throws<GCodeException>(() => code.GetEnum<Shade>('U'))!.Message,
                            Is.EqualTo("parameter 'U' too low"), "and one below every member is too low");

                Assert.That(code.GetEnum<Shade>('Q', (Shade)9), Is.EqualTo((Shade)9),
                            "the limits are about the line, so they do not reach the default");

                // An enumeration whose lowest member is negative moves the line between the two
                Assert.That(Assert.Throws<GCodeException>(() => code.GetEnum<Offset>('U'))!.Message,
                            Is.EqualTo("parameter 'U' too low"), "-2 is below Offset.Below");
                Assert.That(code.GetEnum<Offset>('S'), Is.EqualTo(Offset.Above), "and 1 is a member of it");
                Assert.That(Assert.Throws<GCodeException>(() => code.GetEnum<Offset>('T'))!.Message,
                            Is.EqualTo("parameter 'T' too high"), "while 3 is above Offset.Above");

                Assert.That(Assert.Throws<GCodeException>(() => code.GetEnum<Shade>('V'))!.Message,
                            Is.EqualTo("expected number after 'V'"),
                            "a letter written with nothing after it is a parse error, not a missing parameter");
            });

            Assert.Multiple(() =>
            {
                Assert.That(code.TryGetEnum('S', out Shade seen), Is.True);
                Assert.That(seen, Is.EqualTo(Shade.Dim), "the Try form writes the member it read");

                Assert.That(code.TryGetEnum('Q', out Shade absent), Is.False);
                Assert.That(absent, Is.EqualTo(default(Shade)), "and leaves an absent letter at the default");

                Assert.That(code.TryGetEnum('S', out Shade? nullableSeen), Is.True);
                Assert.That(nullableSeen, Is.EqualTo(Shade.Dim));
                Assert.That(code.TryGetEnum('Q', out Shade? nullableAbsent), Is.False);
                Assert.That(nullableAbsent, Is.Null, "the nullable form says nothing was there rather than zero");

                Assert.That(Assert.Throws<GCodeException>(() => code.TryGetEnum('T', out Shade _))!.Message,
                            Is.EqualTo("parameter 'T' too high"),
                            "the Try form is about whether the letter is there, not about whether its value is usable");
            });
        }
    }

    /// <summary>
    /// GetEnumArray and TryGetEnumArray read a list of members and hold every item to the
    /// enumeration, on top of the length rules every array read has
    /// </summary>
    /// <remarks>
    /// One value per drive, each selecting one of a fixed set of behaviours, is what these are for.
    /// The list stands or falls together, so the first item naming no member refuses the code and
    /// the refusal names the parameter, as the limit checks do for the numeric arrays
    /// </remarks>
    [Test]
    public void EnumArrayAccessors()
    {
        foreach (DuetAPI.Commands.Code code in Parse("M1 S0:1:2 T1:3 U1:-2 W1 V"))
        {
            Assert.Multiple(() =>
            {
                Assert.That(code.GetEnumArray<Shade>('S', 3),
                            Is.EqualTo(new[] { Shade.Off, Shade.Dim, Shade.Bright }), "every value names its member");
                Assert.That(code.GetEnumArray<Shade>('Q', 3, [Shade.Bright]), Is.EqualTo(new[] { Shade.Bright }),
                            "the default answers an absent letter");
                Assert.That(code.GetEnumArray<Shade>('Q', 3, [(Shade)9]), Is.EqualTo(new[] { (Shade)9 }),
                            "and is returned as it stands, because it did not come from the line");
                Assert.That(Assert.Throws<MissingParameterException>(() => code.GetEnumArray<Shade>('Q', 3))!.Letter,
                            Is.EqualTo('Q'), "with no default the letter has to be there");

                Assert.That(Assert.Throws<GCodeException>(() => code.GetEnumArray<Shade>('T', 2))!.Message,
                            Is.EqualTo("parameter 'T' too high"), "an item above every member refuses the list");
                Assert.That(Assert.Throws<GCodeException>(() => code.GetEnumArray<Shade>('U', 2))!.Message,
                            Is.EqualTo("parameter 'U' too low"), "and so does one below every member");
                Assert.That(Assert.Throws<GCodeException>(
                                () => code.GetEnumArray<Shade>('T', 2, errorString: value => $"Invalid shade {value}"))!.Message,
                            Is.EqualTo("Invalid shade 3"),
                            "the refusal is built from the first item that named no member, not from the whole list");

                Assert.That(code.GetEnumArray<Shade>('W', 3, pad: true),
                            Is.EqualTo(new[] { Shade.Dim, Shade.Dim, Shade.Dim }), "one value stands for all of them");
                Assert.That(Assert.Throws<GCodeException>(() => code.GetEnumArray<Shade>('W', 3, exactLength: true))!.Message,
                            Is.EqualTo("Wrong number of values in array, expected 3"),
                            "a caller that needs three and pads for none of them refuses one value");

                Assert.That(Assert.Throws<GCodeException>(() => code.GetEnumArray<Shade>('S', 2))!.Message,
                            Is.EqualTo("array too long for parameter 'S'"),
                            "the length is checked before the members are, as it is checked before any value");
                Assert.That(Assert.Throws<GCodeException>(() => code.GetEnumArray<Shade>('T', 0))!.Message,
                            Is.EqualTo("array too long for parameter 'T'"),
                            "a caller that can take nothing refuses the letter before its value");

                Assert.That(Assert.Throws<GCodeException>(() => code.GetEnumArray<Shade>('V', 3))!.Message,
                            Is.EqualTo("expected number after 'V'"),
                            "a letter written with nothing after it is a parse error, not a missing parameter");
            });

            Assert.Multiple(() =>
            {
                Assert.That(code.TryGetEnumArray('S', 3, out Shade[]? seen), Is.True);
                Assert.That(seen, Is.EqualTo(new[] { Shade.Off, Shade.Dim, Shade.Bright }),
                            "the Try form writes the members it read");

                Assert.That(code.TryGetEnumArray('Q', 3, out Shade[]? absent), Is.False);
                Assert.That(absent, Is.Null, "and leaves null behind for an absent letter");

                Assert.That(code.TryGetEnumArray('W', 3, out Shade[]? padded, pad: true), Is.True);
                Assert.That(padded, Is.EqualTo(new[] { Shade.Dim, Shade.Dim, Shade.Dim }));

                Assert.That(Assert.Throws<GCodeException>(() => code.TryGetEnumArray('T', 2, out Shade[]? _))!.Message,
                            Is.EqualTo("parameter 'T' too high"),
                            "the Try form is about whether the letter is there, not about whether its values are usable");
                Assert.That(Assert.Throws<GCodeException>(() => code.TryGetEnumArray('S', 2, out Shade[]? _))!.Message,
                            Is.EqualTo("array too long for parameter 'S'"));
            });
        }
    }

    /// <summary>
    /// A letter written with no list after it is the empty list when the caller allows it, and the
    /// parse error it always was when the caller does not
    /// </summary>
    /// <remarks>
    /// <c>M584 U</c> releases the drivers of U, which is how a drive gives up the ones it has
    /// (rrf-differences.md section 3.1). The letter is there, so the code means to set something,
    /// and what it names is nothing. Only a caller that has something to do with that reads it this
    /// way, which is why it is asked for rather than assumed
    /// </remarks>
    [Test]
    public void ALetterWithNoListMeansNoneWhenTheCallerAllowsIt()
    {
        DuetAPI.Commands.Code code = new("M584 U E1.0");

        // M584's letters parse as driver identifiers, so the numeric reads are held against a code
        // whose letters are numbers
        DuetAPI.Commands.Code numeric = new("M92 U X20:40");

        Assert.Multiple(() =>
        {
            Assert.That(code.GetDriverIdArray('U', 8, allowZeroLength: true), Is.Empty,
                        "the letter names no drivers");
            Assert.That(code.TryGetDriverIdArray('U', 8, out DriverId[]? released, allowZeroLength: true), Is.True,
                        "the Try shape finds the letter rather than reading it as absent");
            Assert.That(released, Is.Empty);
            Assert.That(code.GetDriverIdArray('E', 8, allowZeroLength: true), Is.EqualTo(new DriverId[] { new(1, 0) }),
                        "and a letter that does carry a list is read as it always was");

            Assert.That(Assert.Throws<GCodeException>(() => code.GetDriverIdArray('U', 8))!.Message,
                        Is.EqualTo("expected number after 'U'"),
                        "without the allowance it is the parse error every other accessor gives");
            Assert.That(code.TryGetDriverIdArray('Z', 8, out DriverId[]? _, allowZeroLength: true), Is.False,
                        "a letter that is not in the code at all is absent rather than empty");
            Assert.That(Assert.Throws<MissingParameterException>(() => code.GetDriverIdArray('Z', 8, allowZeroLength: true))!.Letter,
                        Is.EqualTo('Z'), "and with no default it still has to be there");

            Assert.That(numeric.GetFloatArray('U', 2, allowZeroLength: true), Is.Empty,
                        "every array accessor reads it the same way");
            Assert.That(numeric.GetFloatArray('U', 2, pad: true, exactLength: true, allowZeroLength: true), Is.Empty,
                        "and the length rules are about a list that was given, which this letter was not");
            Assert.That(numeric.GetFloatArray('X', 2, exactLength: true, allowZeroLength: true),
                        Is.EqualTo(new[] { 20.0f, 40.0f }), "while a list that was given is still held to them");
        });
    }

    /// <summary>
    /// An enum list can pass over the values this version does not recognise, for the parameters
    /// that are opt-ins rather than members
    /// </summary>
    /// <remarks>
    /// M500 P is the one such parameter: RepRapFirmware's switch over it ends in
    /// <c>default: break</c>, so a number naming no option is a newer firmware's rather than a
    /// mistake. What comes back is what the line asked for and this version understands, so the
    /// unrecognised values are dropped rather than left as a member nobody named
    /// </remarks>
    [Test]
    public void AnEnumListCanPassOverWhatItDoesNotRecognise()
    {
        DuetAPI.Commands.Code code = new("M1 S0:9:2 T9");

        Assert.Multiple(() =>
        {
            Assert.That(code.GetEnumArray<Shade>('S', 3, ignoreInvalid: true),
                        Is.EqualTo(new[] { Shade.Off, Shade.Bright }), "the value naming no member is passed over");
            Assert.That(code.GetEnumArray<Shade>('T', 3, ignoreInvalid: true), Is.Empty,
                        "a list of nothing it recognises comes back empty rather than refused");
            Assert.That(Assert.Throws<GCodeException>(() => code.GetEnumArray<Shade>('S', 3))!.Message,
                        Is.EqualTo("parameter 'S' too high"), "without it the list is refused as it stands");

            Assert.That(code.TryGetEnumArray('S', 3, out Shade[]? seen, ignoreInvalid: true), Is.True);
            Assert.That(seen, Is.EqualTo(new[] { Shade.Off, Shade.Bright }));
            Assert.That(Assert.Throws<GCodeException>(() => code.GetEnumArray<Shade>('S', 2, ignoreInvalid: true))!.Message,
                        Is.EqualTo("array too long for parameter 'S'"),
                        "the length is still about what the line said, before any of it is read as members");
        });
    }

    /// <summary>
    /// Every accessor that refuses a value builds the refusal from the value it would not take
    /// </summary>
    /// <remarks>
    /// "too low" and "too high" describe a limit, and some codes refuse by naming what was wrong
    /// instead: RepRapFirmware's M574 answers an unknown endstop type with "Invalid endstop input
    /// type", and its M558 names the probe type it would not take - "Invalid Z probe type 4". The
    /// second is why this is a function of the value rather than a string: a handler that had to name
    /// the value would otherwise have to read it itself before asking for it
    /// </remarks>
    [Test]
    public void OverriddenRefusals()
    {
        foreach (DuetAPI.Commands.Code code in Parse("M1 S9 T-9 U1.5 V1:9"))
        {
            Assert.Multiple(() =>
            {
                Assert.That(Assert.Throws<GCodeException>(
                                () => code.GetInt('S', min: 0, max: 4, errorString: value => $"Invalid mode {value}"))!.Message,
                            Is.EqualTo("Invalid mode 9"), "the refusal replaces \"too high\" and names the value");
                Assert.That(Assert.Throws<GCodeException>(
                                () => code.GetInt('T', min: 0, max: 4, errorString: value => $"Invalid mode {value}"))!.Message,
                            Is.EqualTo("Invalid mode -9"),
                            "and \"too low\", since a code that refuses by name refuses both ends alike");
                Assert.That(code.GetInt('S', min: 0, max: 9, errorString: _ => "Invalid mode"), Is.EqualTo(9),
                            "a value inside the limits never builds one");

                Assert.That(Assert.Throws<GCodeException>(
                                () => code.GetFloat('U', min: 0.0f, max: 1.0f, errorString: value => $"Invalid ratio {value}"))!.Message,
                            Is.EqualTo($"Invalid ratio {1.5f}"),
                            "the float accessors hand over a float, formatted by whoever builds the refusal");
                Assert.That(Assert.Throws<GCodeException>(
                                () => code.GetFloat('U', min: 0.0f, max: 1.0f, errorString: value => $"Invalid ratio {value}"))!.Column,
                            Is.EqualTo(CodeParameter.NoColumn),
                            "and the refusal replaces the column a limited read quotes with it, being a "
                            + "sentence the handler wrote rather than a position in the line");
                Assert.That(Assert.Throws<GCodeException>(
                                () => code.GetInt('S', min: 0, max: 4, errorString: value => $"Invalid mode {value}"))!.Column,
                            Is.EqualTo(CodeParameter.NoColumn), "whichever type read the value");
                Assert.That(Assert.Throws<GCodeException>(
                                () => code.GetIntArray('V', 3, min: 0, max: 4, errorString: value => $"Invalid list entry {value}"))!.Message,
                            Is.EqualTo("Invalid list entry 9"),
                            "and the array ones hand over the first item outside the limits, not the whole array");

                Assert.That(Assert.Throws<GCodeException>(
                                () => code.GetEnum<Shade>('S', errorString: value => $"Invalid shade {value}"))!.Message,
                            Is.EqualTo("Invalid shade 9"), "GetEnum names the value that matched no member");
                Assert.That(Assert.Throws<GCodeException>(
                                () => code.TryGetEnum('S', out Shade _, errorString: value => $"Invalid shade {value}"))!.Message,
                            Is.EqualTo("Invalid shade 9"), "as does the Try form");

                Assert.That(Assert.Throws<MissingParameterException>(
                                () => code.GetInt('Q', min: 0, max: 4, errorString: _ => "Invalid mode"))!.Letter,
                            Is.EqualTo('Q'),
                            "an absent letter is a missing parameter rather than a refused value, so the refusal "
                            + "never stands in for the letter itself");
            });
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
