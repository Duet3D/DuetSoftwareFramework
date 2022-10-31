using DuetAPI.Commands;
using NUnit.Framework;
using System.Threading.Tasks;

namespace UnitTests.Machine
{
    [TestFixture]
    public class Expressions
    {
        [Test]
        public void HasSbcExpressions()
        {
            Assert.Throws<CodeParserException>(() => new DuetControlServer.Commands.Code("G1 Z{move.axes[0].machinePosition -"));
            Assert.Throws<CodeParserException>(() => new DuetControlServer.Commands.Code("G92 Z{{3 + 3} + (volumes[0].freeSpace - 4}"));
            Assert.Throws<CodeParserException>(() => new DuetControlServer.Commands.Code("G92 Z{{3 + 3} + (volumes[0].freeSpace - 4)"));
            Assert.Throws<CodeParserException>(() => new DuetControlServer.Commands.Code("G92 Z{{3 + 3 + (move.axes[0].userPosition - 4)"));

            DuetControlServer.Commands.Code code = new("G1 Z{move.axes[2].userPosition - 3}");
            Assert.IsFalse(DuetControlServer.Model.Expressions.ContainsSbcFields(code));

            code = new DuetControlServer.Commands.Code("echo {{3 + 3} + (volumes[0].freeSpace - 4)}");
            Assert.IsTrue(DuetControlServer.Model.Expressions.ContainsSbcFields(code));

            code = new DuetControlServer.Commands.Code("G92 Z{{3 + 3} + (volumes[0].freeSpace - 4)}");
            Assert.IsTrue(DuetControlServer.Model.Expressions.ContainsSbcFields(code));

            code = new DuetControlServer.Commands.Code("G92 Z{{3 + 3} + (move.axes[0].userPosition - 4)}");
            Assert.IsFalse(DuetControlServer.Model.Expressions.ContainsSbcFields(code));
        }

        [Test]
        public void IsLinuxExpression()
        {
            Assert.IsFalse(DuetControlServer.Model.Expressions.IsSbcExpression("state", false));
            Assert.IsFalse(DuetControlServer.Model.Expressions.IsSbcExpression("state.status", false));
            Assert.IsTrue(DuetControlServer.Model.Expressions.IsSbcExpression("network.interfaces", false));
            Assert.IsTrue(DuetControlServer.Model.Expressions.IsSbcExpression("volumes", false));
        }

        [Test]
        public async Task EvaluateLinuxOnly()
        {
            DuetControlServer.Model.Provider.Get.Volumes.Clear();
            DuetControlServer.Model.Provider.Get.Volumes.Add(new DuetAPI.ObjectModel.Volume { FreeSpace = 12345 });

            DuetControlServer.Commands.Code code = new("echo volumes[0].freeSpace");
            object result = await DuetControlServer.Model.Expressions.Evaluate(code, false);
            Assert.AreEqual("12345", result);

            code = new DuetControlServer.Commands.Code("echo move.axes[0].userPosition");
            result = await DuetControlServer.Model.Expressions.Evaluate(code, false);
            Assert.AreEqual("move.axes[0].userPosition", result);

            code = new DuetControlServer.Commands.Code("echo move.axes[{1 + 1}].userPosition");
            result = await DuetControlServer.Model.Expressions.Evaluate(code, false);
            Assert.AreEqual("move.axes[{1 + 1}].userPosition", result);

            code = new DuetControlServer.Commands.Code("echo #volumes");
            result = await DuetControlServer.Model.Expressions.Evaluate(code, false);
            Assert.AreEqual("1", result);

            code = new DuetControlServer.Commands.Code("echo volumes");
            Assert.ThrowsAsync<CodeParserException>(async () => await DuetControlServer.Model.Expressions.Evaluate(code, true));

            code = new DuetControlServer.Commands.Code("echo plugins");
            result = await DuetControlServer.Model.Expressions.Evaluate(code, false);
            Assert.AreEqual("{object}", result);

            code = new DuetControlServer.Commands.Code("echo move.axes[0].userPosition + volumes[0].freeSpace");
            result = await DuetControlServer.Model.Expressions.Evaluate(code, false);
            Assert.AreEqual("move.axes[0].userPosition +12345", result);

            code = new DuetControlServer.Commands.Code("echo \"hello\"");
            result = await DuetControlServer.Model.Expressions.Evaluate(code, false);
            Assert.AreEqual("\"hello\"", result);

            code = new DuetControlServer.Commands.Code("echo {\"hello\" ^ (\"there\" + volumes[0].freeSpace)}");
            result = await DuetControlServer.Model.Expressions.Evaluate(code, false);
            Assert.AreEqual("{\"hello\" ^ (\"there\" +12345)}", result);

            DuetControlServer.Model.Expressions.CustomFunctions.Add("fileexists", async (string functionName, object[] vals) =>
            {
                Assert.AreEqual(functionName, "fileexists");
                Assert.AreEqual(vals.Length, 1);
                Assert.AreEqual(vals[0], "0:/sys/config.g");
                return await Task.FromResult(true);
            });

            code = new DuetControlServer.Commands.Code("echo fileexists(\"0:/sys/config.g\")");
            result = await DuetControlServer.Model.Expressions.Evaluate(code, false);
            Assert.AreEqual(result, "true");
        }
    }
}
