using Newtonsoft.Json;
using NUnit.Framework;

namespace DuetUnitTest.Commands
{
    [TestFixture]
    public class Code
    {
        [Test]
        public void Parse()
        {
            DuetAPI.Commands.Code code = new DuetControlServer.Commands.Code("G54.6");
            Assert.AreEqual(DuetAPI.Commands.CodeType.GCode, code.Type);
            Assert.AreEqual(54, code.MajorNumber);
            Assert.AreEqual(6, code.MinorNumber);

            code = new DuetControlServer.Commands.Code("M106 P1 C\"Fancy \"\" Fan\" H-1 S0.5");
            Assert.AreEqual(DuetAPI.Commands.CodeType.MCode, code.Type);
            Assert.AreEqual(106, code.MajorNumber);
            Assert.AreEqual(null, code.MinorNumber);
            Assert.AreEqual(4, code.Parameters.Count);
            Assert.AreEqual('P', code.Parameters[0].Letter);
            Assert.AreEqual(1, code.Parameters[0].AsInt);
            Assert.AreEqual('C', code.Parameters[1].Letter);
            Assert.AreEqual("Fancy \" Fan", code.Parameters[1].AsString);
            Assert.AreEqual('H', code.Parameters[2].Letter);
            Assert.AreEqual(-1, code.Parameters[2].AsInt);
            Assert.AreEqual('S', code.Parameters[3].Letter);
            
            TestContext.Out.Write(JsonConvert.SerializeObject(code, Formatting.Indented));
            Assert.AreEqual(0.5, code.Parameters[3].AsDouble);

            code = new DuetControlServer.Commands.Code("M569 P2 S1 T0.3");
            Assert.AreEqual(DuetAPI.Commands.CodeType.MCode, code.Type);
            Assert.AreEqual(569, code.MajorNumber);
            Assert.AreEqual(null, code.MinorNumber);
            Assert.AreEqual(3, code.Parameters.Count);
            Assert.AreEqual('P', code.Parameters[0].Letter);
            Assert.AreEqual(2, code.Parameters[0].AsInt);
            Assert.AreEqual('S', code.Parameters[1].Letter);
            Assert.AreEqual(1, code.Parameters[1].AsInt);
            Assert.AreEqual('T', code.Parameters[2].Letter);
            Assert.AreEqual(0.3, code.Parameters[2].AsDouble);

            code = new DuetControlServer.Commands.Code("T3 P4 S\"foo\"");
            Assert.AreEqual(DuetAPI.Commands.CodeType.TCode, code.Type);
            Assert.AreEqual(3, code.MajorNumber);
            Assert.AreEqual(2, code.Parameters.Count);
            Assert.AreEqual('P', code.Parameters[0].Letter);
            Assert.AreEqual(4, code.Parameters[0].AsInt);
            Assert.AreEqual('S', code.Parameters[1].Letter);
            Assert.AreEqual("foo", code.Parameters[1].AsString);
            Assert.AreEqual("T3 P4 S\"foo\"", code.ToString());
        }
    }
}
