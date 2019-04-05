using DuetAPI.Machine.MessageBox;
using NUnit.Framework;
using Model = DuetAPI.Machine.Model;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class MessageBox
    {
        [Test]
        public void Clone()
        {
            Model original = new Model();
            original.MessageBox.AxisControls = new int[] { 1, 2 };
            original.MessageBox.Message = "Message";
            original.MessageBox.Mode = MessageBoxMode.OkCancel;
            original.MessageBox.Title = "Title";

            Model clone = (Model)original.Clone();

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
