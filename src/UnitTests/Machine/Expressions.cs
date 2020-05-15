﻿using DuetAPI.Commands;
using NUnit.Framework;
using System.Threading.Tasks;

namespace UnitTests.Machine
{
    [TestFixture]
    public class Expressions
    {
        [Test]
        public void HasLinuxExpressions()
        {
            Assert.Throws<CodeParserException>(() => new DuetControlServer.Commands.Code("G1 Z{move.axes[0].machinePosition -"));
            Assert.Throws<CodeParserException>(() => new DuetControlServer.Commands.Code("G92 Z{{3 + 3} + (volumes[0].freeSpace - 4}"));
            Assert.Throws<CodeParserException>(() => new DuetControlServer.Commands.Code("G92 Z{{3 + 3} + (volumes[0].freeSpace - 4)"));
            Assert.Throws<CodeParserException>(() => new DuetControlServer.Commands.Code("G92 Z{{3 + 3 + (move.axes[0].userPosition - 4)"));

            DuetControlServer.Commands.Code code = new DuetControlServer.Commands.Code("G1 Z{move.axes[2].userPosition - 3}");
            Assert.IsFalse(DuetControlServer.Model.Expressions.ContainsLinuxFields(code));

            code = new DuetControlServer.Commands.Code("echo {{3 + 3} + (volumes[0].freeSpace - 4)}");
            Assert.IsTrue(DuetControlServer.Model.Expressions.ContainsLinuxFields(code));

            code = new DuetControlServer.Commands.Code("G92 Z{{3 + 3} + (volumes[0].freeSpace - 4)}");
            Assert.IsTrue(DuetControlServer.Model.Expressions.ContainsLinuxFields(code));

            code = new DuetControlServer.Commands.Code("G92 Z{{3 + 3} + (move.axes[0].userPosition - 4)}");
            Assert.IsFalse(DuetControlServer.Model.Expressions.ContainsLinuxFields(code));
        }

        [Test]
        public void IsLinuxExpression()
        {
            Assert.IsFalse(DuetControlServer.Model.Expressions.IsLinuxExpression("state"));
            Assert.IsFalse(DuetControlServer.Model.Expressions.IsLinuxExpression("state.status"));
            Assert.IsTrue(DuetControlServer.Model.Expressions.IsLinuxExpression("scanner"));
            Assert.IsTrue(DuetControlServer.Model.Expressions.IsLinuxExpression("network.interfaces"));
            Assert.IsTrue(DuetControlServer.Model.Expressions.IsLinuxExpression("volumes"));
        }

        [Test]
        public async Task EvaluateLinuxOnly()
        {
            DuetControlServer.Model.Provider.Get.Volumes.Clear();
            DuetControlServer.Model.Provider.Get.Volumes.Add(new DuetAPI.Machine.Volume { FreeSpace = 12345 });

            DuetControlServer.Commands.Code code = new DuetControlServer.Commands.Code("echo volumes[0].freeSpace");
            object result = await DuetControlServer.Model.Expressions.Evaluate(code, true);
            Assert.AreEqual("12345", result);

            code = new DuetControlServer.Commands.Code("echo move.axes[0].userPosition");
            result = await DuetControlServer.Model.Expressions.Evaluate(code, true);
            Assert.AreEqual("move.axes[0].userPosition", result);

            code = new DuetControlServer.Commands.Code("echo move.axes[{1 + 1}].userPosition");
            result = await DuetControlServer.Model.Expressions.Evaluate(code, true);
            Assert.AreEqual("move.axes[{1 + 1}].userPosition", result);

            code = new DuetControlServer.Commands.Code("echo #volumes");
            result = await DuetControlServer.Model.Expressions.Evaluate(code, true);
            Assert.AreEqual("1", result);

            code = new DuetControlServer.Commands.Code("echo volumes");
            Assert.ThrowsAsync<CodeParserException>(async () => await DuetControlServer.Model.Expressions.Evaluate(code, true));

            code = new DuetControlServer.Commands.Code("echo scanner");
            result = await DuetControlServer.Model.Expressions.Evaluate(code, false);
            Assert.AreEqual("{object}", result);

            code = new DuetControlServer.Commands.Code("echo move.axes[0].userPosition + volumes[0].freeSpace");
            result = await DuetControlServer.Model.Expressions.Evaluate(code, true);
            Assert.AreEqual("move.axes[0].userPosition + 12345", result);

            code = new DuetControlServer.Commands.Code("echo \"hello\"");
            result = await DuetControlServer.Model.Expressions.Evaluate(code, true);
            Assert.AreEqual("\"hello\"", result);

            code = new DuetControlServer.Commands.Code("echo {\"hello\" ^ (\"there\" + volumes[0].freeSpace)}");
            result = await DuetControlServer.Model.Expressions.Evaluate(code, true);
            Assert.AreEqual("{\"hello\" ^ (\"there\" + 12345)}", result);
        }
    }
}
