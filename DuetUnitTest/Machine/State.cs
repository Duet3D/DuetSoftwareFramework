using DuetAPI.Machine.State;
using NUnit.Framework;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class State
    {
        [Test]
        public void Clone()
        {
            DuetAPI.Machine.Model original = new DuetAPI.Machine.Model();

            original.State.AtxPower = true;
            original.State.CurrentTool = 123;
            original.State.Mode = Mode.Laser;
            original.State.Status = Status.Processing;

            DuetAPI.Machine.Model clone = (DuetAPI.Machine.Model)original.Clone();

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
