using DuetAPI.Machine;
using NUnit.Framework;

namespace UnitTests.Machine
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
            original.State.DisplayMessage = "display message";
            original.State.LogFile = "log file";
            original.State.Mode = MachineMode.Laser;
            original.State.Status = MachineStatus.Processing;

            MachineModel clone = (MachineModel)original.Clone();

            Assert.AreEqual(original.State.AtxPower, clone.State.AtxPower);
            Assert.AreEqual(original.State.CurrentTool, clone.State.CurrentTool);
            Assert.AreEqual(original.State.DisplayMessage, clone.State.DisplayMessage);
            Assert.AreEqual(original.State.LogFile, clone.State.LogFile);
            Assert.AreEqual(original.State.Mode, clone.State.Mode);
            Assert.AreEqual(original.State.Status, clone.State.Status);
        }

        [Test]
        public void Assign()
        {
            MachineModel original = new MachineModel();

            original.State.AtxPower = true;
            original.State.CurrentTool = 123;
            original.State.DisplayMessage = "display message";
            original.State.DisplayMessage = "log file";
            original.State.Mode = MachineMode.Laser;
            original.State.Status = MachineStatus.Processing;

            MachineModel assigned = new MachineModel();
            assigned.Assign(original);

            Assert.AreEqual(original.State.AtxPower, assigned.State.AtxPower);
            Assert.AreEqual(original.State.CurrentTool, assigned.State.CurrentTool);
            Assert.AreEqual(original.State.DisplayMessage, assigned.State.DisplayMessage);
            Assert.AreEqual(original.State.LogFile, assigned.State.LogFile);
            Assert.AreEqual(original.State.Mode, assigned.State.Mode);
            Assert.AreEqual(original.State.Status, assigned.State.Status);
        }
    }
}
