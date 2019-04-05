using DuetAPI.Machine;
using NUnit.Framework;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class Channels
    {
        [Test]
        public void Clone()
        {
            Model original = new Model();

            original.Channels.SPI.RelativePositioning = true;
            original.Channels.USB.Feedrate = 123;
            original.Channels.File.Feedrate = 565;
            original.Channels.File.UsingInches = true;
            original.Channels.HTTP.RelativeExtrusion = true;
            original.Channels.Telnet.Feedrate = 45;
            original.Channels[DuetAPI.CodeChannel.Telnet].StackDepth = 5;

            Model clone = (Model)original.Clone();

            Assert.AreEqual(original.Channels.SPI.RelativePositioning, clone.Channels.SPI.RelativePositioning);
            Assert.AreEqual(original.Channels.USB.Feedrate, clone.Channels.USB.Feedrate);
            Assert.AreEqual(original.Channels.File.Feedrate, clone.Channels.File.Feedrate);
            Assert.AreEqual(original.Channels.File.UsingInches, clone.Channels.File.UsingInches);
            Assert.AreEqual(original.Channels.HTTP.RelativePositioning, clone.Channels.HTTP.RelativePositioning);
            Assert.AreEqual(original.Channels.Telnet.Feedrate, clone.Channels.Telnet.Feedrate);
            Assert.AreEqual(original.Channels.Telnet.StackDepth, clone.Channels.Telnet.StackDepth);
        }
    }
}
