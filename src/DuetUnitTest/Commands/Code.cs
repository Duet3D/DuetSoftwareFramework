using DuetAPI.Commands;
using Newtonsoft.Json;
using NUnit.Framework;

namespace DuetUnitTest.Commands
{
    [TestFixture]
    public class Code
    {
        [Test]
        public void ParseG28()
        {
            DuetAPI.Commands.Code code = new DuetControlServer.Commands.Code("G28 X Y");
            Assert.AreEqual(CodeType.GCode, code.Type);
            Assert.AreEqual(28, code.MajorNumber);
            Assert.AreEqual(null, code.MinorNumber);
            Assert.AreEqual(2, code.Parameters.Count);
            Assert.AreEqual('X', code.Parameters[0].Letter);
            Assert.AreEqual('Y', code.Parameters[1].Letter);
        }

        [Test]
        public void ParseG29()
        {
            DuetAPI.Commands.Code code = new DuetControlServer.Commands.Code("G29 S1 ; load heightmap");
            Assert.AreEqual(CodeType.GCode, code.Type);
            Assert.AreEqual(29, code.MajorNumber);
            Assert.AreEqual(null, code.MinorNumber);
            Assert.AreEqual(1, code.Parameters.Count);
            Assert.AreEqual('S', code.Parameters[0].Letter);
            Assert.IsTrue(code.Parameter('S', 0) == 1);
        }

        [Test]
        public void ParseG54()
        {
            DuetAPI.Commands.Code code = new DuetControlServer.Commands.Code("G54.6");
            Assert.AreEqual(CodeType.GCode, code.Type);
            Assert.AreEqual(54, code.MajorNumber);
            Assert.AreEqual(6, code.MinorNumber);
        }

        [Test]
        public void ParseM106()
        {
            DuetAPI.Commands.Code code = new DuetControlServer.Commands.Code("M106 P1 C\"Fancy \"\" Fan\" H-1 S0.5");
            Assert.AreEqual(CodeType.MCode, code.Type);
            Assert.AreEqual(106, code.MajorNumber);
            Assert.AreEqual(null, code.MinorNumber);
            Assert.AreEqual(4, code.Parameters.Count);
            Assert.AreEqual('P', code.Parameters[0].Letter);
            Assert.AreEqual(1, (int)code.Parameters[0]);
            Assert.AreEqual('C', code.Parameters[1].Letter);
            Assert.AreEqual("Fancy \" Fan", (string)code.Parameters[1]);
            Assert.AreEqual('H', code.Parameters[2].Letter);
            Assert.AreEqual(-1, (int)code.Parameters[2]);
            Assert.AreEqual('S', code.Parameters[3].Letter);
            Assert.AreEqual(0.5, code.Parameters[3], 0.0001);

            TestContext.Out.Write(JsonConvert.SerializeObject(code, Formatting.Indented));
        }

        [Test]
        public void ParseM563()
        {
            DuetAPI.Commands.Code code = new DuetAPI.Commands.Code("M563 P0 D0:1 H1:2                             ; Define tool 0");
            Assert.AreEqual(CodeType.MCode, code.Type);
            Assert.AreEqual(563, code.MajorNumber);
            Assert.AreEqual(null, code.MinorNumber);
            Assert.AreEqual(3, code.Parameters.Count);
            Assert.AreEqual('P', code.Parameters[0].Letter);
            Assert.AreEqual(0, (int)code.Parameters[0]);
            Assert.AreEqual('D', code.Parameters[1].Letter);
            Assert.AreEqual(new int[] { 0, 1 }, (int[])code.Parameters[1]);
            Assert.AreEqual('H', code.Parameters[2].Letter);
            Assert.AreEqual(new int[] { 1, 2 }, (int[])code.Parameters[2]);
            Assert.AreEqual(" Define tool 0", code.Comment);
        }

        [Test]
        public void ParseM569()
        {
            DuetAPI.Commands.Code code = new DuetControlServer.Commands.Code("M569 P2 S1 T0.5");
            Assert.AreEqual(CodeType.MCode, code.Type);
            Assert.AreEqual(569, code.MajorNumber);
            Assert.AreEqual(null, code.MinorNumber);
            Assert.AreEqual(false, code.EnforceAbsoluteCoordinates);
            Assert.AreEqual(3, code.Parameters.Count);
            Assert.AreEqual('P', code.Parameters[0].Letter);
            Assert.AreEqual(2, (int)code.Parameters[0]);
            Assert.AreEqual('S', code.Parameters[1].Letter);
            Assert.AreEqual(1, (int)code.Parameters[1]);
            Assert.AreEqual('T', code.Parameters[2].Letter);
            Assert.AreEqual(0.5, code.Parameters[2], 0.0001);
        }

        [Test]
        public void ParseM574()
        {
            DuetAPI.Commands.Code code = new DuetAPI.Commands.Code("M574 Y2 S1 P\"io1.in\";comment");
            Assert.AreEqual(CodeType.MCode, code.Type);
            Assert.AreEqual(574, code.MajorNumber);
            Assert.AreEqual(null, code.MinorNumber);
            Assert.AreEqual(false, code.EnforceAbsoluteCoordinates);
            Assert.AreEqual(3, code.Parameters.Count);
            Assert.AreEqual('Y', code.Parameters[0].Letter);
            Assert.AreEqual(2, (int)code.Parameters[0]);
            Assert.AreEqual('S', code.Parameters[1].Letter);
            Assert.AreEqual(1, (int)code.Parameters[1]);
            Assert.AreEqual('P', code.Parameters[2].Letter);
            Assert.AreEqual("io1.in", (string)code.Parameters[2]);
            Assert.AreEqual("comment", code.Comment);
        }

        [Test]
        public void ParseT3()
        {
            DuetAPI.Commands.Code code = new DuetControlServer.Commands.Code("T3 P4 S\"foo\"");
            Assert.AreEqual(CodeType.TCode, code.Type);
            Assert.AreEqual(3, code.MajorNumber);
            Assert.AreEqual(null, code.MinorNumber);
            Assert.AreEqual(false, code.EnforceAbsoluteCoordinates);
            Assert.AreEqual(2, code.Parameters.Count);
            Assert.AreEqual('P', code.Parameters[0].Letter);
            Assert.AreEqual(4, (int)code.Parameters[0]);
            Assert.AreEqual('S', code.Parameters[1].Letter);
            Assert.AreEqual("foo", (string)code.Parameters[1]);
            Assert.AreEqual("T3 P4 S\"foo\"", code.ToString());
        }

        [Test]
        public void ParseAbsoluteG1()
        {
            DuetAPI.Commands.Code code = new DuetAPI.Commands.Code("G53 G1 X3 Y1.25");
            Assert.AreEqual(true, code.EnforceAbsoluteCoordinates);
            Assert.AreEqual(1, code.MajorNumber);
            Assert.AreEqual(null, code.MinorNumber);
            Assert.AreEqual(2, code.Parameters.Count);
            Assert.AreEqual('X', code.Parameters[0].Letter);
            Assert.AreEqual(3, (int)code.Parameters[0]);
            Assert.AreEqual('Y', code.Parameters[1].Letter);
            Assert.AreEqual(1.25, code.Parameters[1], 0.0001);
        }

        [Test]
        public void ParseQuotedM32()
        {
            DuetAPI.Commands.Code code = new DuetAPI.Commands.Code("M32 \"foo bar.g\"");
            Assert.AreEqual(CodeType.MCode, code.Type);
            Assert.AreEqual(32, code.MajorNumber);
            Assert.AreEqual("foo bar.g", code.GetUnprecedentedString());
        }

        [Test]
        public void ParseUnquotedM32()
        {
            DuetAPI.Commands.Code code = new DuetAPI.Commands.Code("M32 foo bar.g");
            Assert.AreEqual(CodeType.MCode, code.Type);
            Assert.AreEqual(32, code.MajorNumber);
            Assert.AreEqual("foo bar.g", code.GetUnprecedentedString());
        }

        [Test]
        public void ParseM586WithComment()
        {
            DuetAPI.Commands.Code code = new DuetAPI.Commands.Code("M586 P2 S0                               ; Disable Telnet");
            Assert.AreEqual(CodeType.MCode, code.Type);
            Assert.AreEqual(586, code.MajorNumber);
            Assert.AreEqual(null, code.MinorNumber);
            Assert.AreEqual(2, code.Parameters.Count);
            Assert.AreEqual('P', code.Parameters[0].Letter);
            Assert.AreEqual(2, (int)code.Parameters[0]);
            Assert.AreEqual('S', code.Parameters[1].Letter);
            Assert.AreEqual(0, (int)code.Parameters[1]);
            Assert.AreEqual(" Disable Telnet", code.Comment);
        }
    }
}
