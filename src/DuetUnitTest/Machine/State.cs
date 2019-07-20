using DuetAPI.Machine;
using NUnit.Framework;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class State
    {
        [Test]
        public void Clone()
        {
            MachineModel original = new MachineModel();

            original.State.AtxPower = true;
            original.State.CurrentTool = 123;
            original.State.Mode = MachineMode.Laser;
            original.State.Status = MachineStatus.Processing;

            MachineModel clone = (MachineModel)original.Clone();

            Assert.AreEqual(original.State.AtxPower, clone.State.AtxPower);
            Assert.AreEqual(original.State.CurrentTool, clone.State.CurrentTool);
            Assert.AreEqual(original.State.Mode, clone.State.Mode);
            Assert.AreEqual(original.State.Status, clone.State.Status);

            Assert.AreNotSame(original.State.AtxPower, clone.State.AtxPower);
            Assert.AreNotSame(original.State.CurrentTool, clone.State.CurrentTool);
            Assert.AreNotSame(original.State.Mode, clone.State.Mode);
            Assert.AreNotSame(original.State.Status, clone.State.Status);
        }

        [Test]
        public void Assign()
        {
            MachineModel original = new MachineModel();

            original.State.AtxPower = true;
            original.State.CurrentTool = 123;
            original.State.Mode = MachineMode.Laser;
            original.State.Status = MachineStatus.Processing;

            MachineModel clone = new MachineModel();
            clone.Assign(original);

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
