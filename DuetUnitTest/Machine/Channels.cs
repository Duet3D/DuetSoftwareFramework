using NUnit.Framework;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class Channels
    {
        [Test]
        public void Clone()
        {
            DuetAPI.Machine.Model original = new DuetAPI.Machine.Model();

            original.Channels.Main.RelativePositioning = true;
            original.Channels.Serial.Feedrate = 123;
            original.Channels.File.Feedrate = 565;
            original.Channels.HTTP.RelativeExtrusion = true;
            original.Channels.Telnet.Feedrate = 45;

            DuetAPI.Machine.Model clone = (DuetAPI.Machine.Model)original.Clone();

            Assert.AreEqual(original.Channels.Main.RelativePositioning, clone.Channels.Main.RelativePositioning);
            Assert.AreEqual(original.Channels.Serial.Feedrate, clone.Channels.Serial.Feedrate);
            Assert.AreEqual(original.Channels.File.Feedrate, clone.Channels.File.Feedrate);
            Assert.AreEqual(original.Channels.HTTP.RelativePositioning, clone.Channels.HTTP.RelativePositioning);
            Assert.AreEqual(original.Channels.Telnet.Feedrate, clone.Channels.Telnet.Feedrate);
        }
    }
}
