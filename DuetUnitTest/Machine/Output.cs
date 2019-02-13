using DuetAPI;
using NUnit.Framework;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class Output
    {
        [Test]
        public void Clone()
        {
            DuetAPI.Machine.Model original = new DuetAPI.Machine.Model();

            original.Output.Add(new Message
            {
                Content = "Test 1 2 3",
                Type = Message.Warning
            });

            original.Output.Add(new Message
            {
                Content = "Err 3 2 1",
                Type = Message.Error
            });

            DuetAPI.Machine.Model clone = (DuetAPI.Machine.Model)original.Clone();

            Assert.AreEqual(2, original.Output.Count);
            Assert.AreEqual(original.Output[0].Content, "Test 1 2 3");
            Assert.AreEqual(original.Output[0].Type, Message.Warning);
            Assert.AreEqual(original.Output[1].Content, "Err 3 2 1");
            Assert.AreEqual(original.Output[1].Type, Message.Error);
        }
    }
}
