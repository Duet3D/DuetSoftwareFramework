using DuetAPI.Machine.State;
using NUnit.Framework;
using Model = DuetAPI.Machine.Model;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class State
    {
        [Test]
        public void Clone()
        {
            Model original = new Model();

            original.State.AtxPower = true;
            original.State.CurrentTool = 123;
            original.State.Mode = Mode.Laser;
            original.State.Status = Status.Processing;

            Model clone = (Model)original.Clone();

            Assert.AreEqual(original.State.AtxPower, clone.State.AtxPower);
            Assert.AreEqual(original.State.CurrentTool, clone.State.CurrentTool);
            Assert.AreEqual(original.State.Mode, clone.State.Mode);
            Assert.AreEqual(original.State.Status, clone.State.Status);

            Assert.AreNotSame(original.State.AtxPower, clone.State.AtxPower);
            Assert.AreNotSame(original.State.CurrentTool, clone.State.CurrentTool);
            Assert.AreNotSame(original.State.Mode, clone.State.Mode);
            Assert.AreNotSame(original.State.Status, clone.State.Status);
        }
    }
}
