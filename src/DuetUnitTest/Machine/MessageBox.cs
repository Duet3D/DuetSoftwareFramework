using DuetAPI.Machine;
using NUnit.Framework;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class MessageBox
    {
        [Test]
        public void Clone()
        {
            MachineModel original = new MachineModel();
            original.MessageBox.AxisControls.Add(1);
            original.MessageBox.AxisControls.Add(2);
            original.MessageBox.Message = "Message";
            original.MessageBox.Mode = MessageBoxMode.OkCancel;
            original.MessageBox.Title = "Title";

            MachineModel clone = (MachineModel)original.Clone();

            Assert.AreEqual(original.MessageBox.AxisControls, clone.MessageBox.AxisControls);
            Assert.AreEqual(original.MessageBox.Message, clone.MessageBox.Message);
            Assert.AreEqual(original.MessageBox.Mode, clone.MessageBox.Mode);
            Assert.AreEqual(original.MessageBox.Title, clone.MessageBox.Title);

            Assert.AreNotSame(original.MessageBox.AxisControls, clone.MessageBox.AxisControls);
            Assert.AreNotSame(original.MessageBox.Message, clone.MessageBox.Message);
            Assert.AreNotSame(original.MessageBox.Mode, clone.MessageBox.Mode);
            Assert.AreNotSame(original.MessageBox.Title, clone.MessageBox.Title);
        }

        [Test]
        public void Assign()
        {
            MachineModel original = new MachineModel();
            original.MessageBox.AxisControls.Add(1);
            original.MessageBox.AxisControls.Add(2);
            original.MessageBox.Message = "Message";
            original.MessageBox.Mode = MessageBoxMode.OkCancel;
            original.MessageBox.Title = "Title";

            MachineModel assigned = new MachineModel();
            assigned.Assign(original);

            Assert.AreEqual(original.MessageBox.AxisControls, assigned.MessageBox.AxisControls);
            Assert.AreEqual(original.MessageBox.Message, assigned.MessageBox.Message);
            Assert.AreEqual(original.MessageBox.Mode, assigned.MessageBox.Mode);
            Assert.AreEqual(original.MessageBox.Title, assigned.MessageBox.Title);

            Assert.AreNotSame(original.MessageBox.AxisControls, assigned.MessageBox.AxisControls);
            Assert.AreNotSame(original.MessageBox.Message, assigned.MessageBox.Message);
            Assert.AreNotSame(original.MessageBox.Mode, assigned.MessageBox.Mode);
            Assert.AreNotSame(original.MessageBox.Title, assigned.MessageBox.Title);
        }
    }
}
