using DuetAPI;
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
            Assert.That(DuetControlServer.Model.Expressions.ContainsSbcFields(code), Is.False);

            code = new DuetControlServer.Commands.Code("echo {{3 + 3} + (volumes[0].freeSpace - 4)}");
            Assert.That(DuetControlServer.Model.Expressions.ContainsSbcFields(code), Is.True);

            code = new DuetControlServer.Commands.Code("G92 Z{{3 + 3} + (volumes[0].freeSpace - 4)}");
            Assert.That(DuetControlServer.Model.Expressions.ContainsSbcFields(code), Is.True);

            code = new DuetControlServer.Commands.Code("G92 Z{{3 + 3} + (move.axes[0].userPosition - 4)}");
            Assert.That(DuetControlServer.Model.Expressions.ContainsSbcFields(code), Is.False);
        }

        [Test]
        public void IsLinuxExpression()
        {
            Assert.That(DuetControlServer.Model.Expressions.IsSbcExpression("state", false), Is.False);
            Assert.That(DuetControlServer.Model.Expressions.IsSbcExpression("state.status", false), Is.False);
            Assert.That(DuetControlServer.Model.Expressions.IsSbcExpression("network.interfaces", false), Is.True);
            Assert.That(DuetControlServer.Model.Expressions.IsSbcExpression("volumes", false), Is.True);
        }

        [Test]
        public async Task EvaluateLinuxOnly()
        {
            DuetControlServer.Model.Provider.Get.Volumes.Clear();
            DuetControlServer.Model.Provider.Get.Volumes.Add(new DuetAPI.ObjectModel.Volume { FreeSpace = 12345 });

            DuetControlServer.Commands.Code code = new("echo volumes[0].freeSpace");
            object result = await DuetControlServer.Model.Expressions.Evaluate(code, false);
            Assert.That(result, Is.EqualTo("12345"));

            code = new DuetControlServer.Commands.Code("echo move.axes[0].userPosition");
            result = await DuetControlServer.Model.Expressions.Evaluate(code, false);
            Assert.That(result, Is.EqualTo("move.axes[0].userPosition"));

            code = new DuetControlServer.Commands.Code("echo move.axes[{1 + 1}].userPosition");
            result = await DuetControlServer.Model.Expressions.Evaluate(code, false);
            Assert.That(result, Is.EqualTo("move.axes[{1 + 1}].userPosition"));

            code = new DuetControlServer.Commands.Code("echo #volumes");
            result = await DuetControlServer.Model.Expressions.Evaluate(code, false);
            Assert.That(result, Is.EqualTo("1"));

            code = new DuetControlServer.Commands.Code("echo volumes");
            Assert.ThrowsAsync<CodeParserException>(async () => await DuetControlServer.Model.Expressions.Evaluate(code, true));

            code = new DuetControlServer.Commands.Code("echo plugins");
            result = await DuetControlServer.Model.Expressions.Evaluate(code, false);
            Assert.That(result, Is.EqualTo("{object}"));

            code = new DuetControlServer.Commands.Code("echo move.axes[0].userPosition + volumes[0].freeSpace");
            result = await DuetControlServer.Model.Expressions.Evaluate(code, false);
            Assert.That(result, Is.EqualTo("move.axes[0].userPosition +12345"));

            code = new DuetControlServer.Commands.Code("echo \"hello\"");
            result = await DuetControlServer.Model.Expressions.Evaluate(code, false);
            Assert.That(result, Is.EqualTo("\"hello\""));

            code = new DuetControlServer.Commands.Code("echo {\"hello\" ^ (\"there\" + volumes[0].freeSpace)}");
            result = await DuetControlServer.Model.Expressions.Evaluate(code, false);
            Assert.That(result, Is.EqualTo("{\"hello\" ^ (\"there\" +12345)}"));

            DuetControlServer.Model.Expressions.CustomFunctions.Add("fileexists", async (CodeChannel channel, string functionName, object[] vals) =>
            {
                Assert.That(functionName, Is.EqualTo("fileexists"));
                Assert.That(vals.Length, Is.EqualTo(1));
                Assert.That(vals[0], Is.EqualTo("0:/sys/config.g"));
                return await Task.FromResult(true);
            });

            code = new DuetControlServer.Commands.Code("echo fileexists(\"0:/sys/config.g\")");
            result = await DuetControlServer.Model.Expressions.Evaluate(code, false);
            Assert.That(result, Is.EqualTo("true"));
        }
    }
}
