using NUnit.Framework;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class MessageBox
    {
        [Test]
        public void Clone()
        {
            DuetAPI.Machine.Model original = new DuetAPI.Machine.Model();
            original.MessageBox.AxisControls = new uint[] { 1, 2 };
            original.MessageBox.Message = "Message";
            original.MessageBox.Mode = DuetAPI.Machine.MessageBox.MessageBoxMode.OkCancel;
            original.MessageBox.Title = "Title";

            DuetAPI.Machine.Model clone = (DuetAPI.Machine.Model)original.Clone();

            Assert.AreEqual(original.MessageBox.AxisControls, clone.MessageBox.AxisControls);
            Assert.AreEqual(original.MessageBox.Message, clone.MessageBox.Message);
            Assert.AreEqual(original.MessageBox.Mode, clone.MessageBox.Mode);
            Assert.AreEqual(original.MessageBox.Title, clone.MessageBox.Title);

            Assert.AreNotSame(original.MessageBox.AxisControls, clone.MessageBox.AxisControls);
            Assert.AreNotSame(original.MessageBox.Message, clone.MessageBox.Message);
            Assert.AreNotSame(original.MessageBox.Mode, clone.MessageBox.Mode);
            Assert.AreNotSame(original.MessageBox.Title, clone.MessageBox.Title);
        }
    }
}
