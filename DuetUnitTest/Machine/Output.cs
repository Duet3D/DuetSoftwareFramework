using DuetAPI;
using DuetAPI.Machine;
using NUnit.Framework;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class Output
    {
        [Test]
        public void Clone()
        {
            Model original = new Model();

            original.Messages.Add(new Message(MessageType.Warning, "Test 1 2 3"));
            original.Messages.Add(new Message(MessageType.Error, "Err 3 2 1"));

            Model clone = (Model)original.Clone();

            Assert.AreEqual(2, original.Messages.Count);
            Assert.AreEqual(original.Messages[0].Content, "Test 1 2 3");
            Assert.AreEqual(original.Messages[0].Type, MessageType.Warning);
            Assert.AreEqual(original.Messages[1].Content, "Err 3 2 1");
            Assert.AreEqual(original.Messages[1].Type, MessageType.Error);
        }
    }
}
