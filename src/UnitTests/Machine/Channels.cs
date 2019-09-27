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
            MachineModel original = new MachineModel();

            original.Channels.SPI.RelativePositioning = true;
            original.Channels.USB.Feedrate = 123F;
            original.Channels.File.Feedrate = 565F;
            original.Channels.CodeQueue.UsingInches = true;
            original.Channels.HTTP.RelativeExtrusion = true;
            original.Channels.Daemon.Feedrate = 45F;
            original.Channels[DuetAPI.CodeChannel.Telnet].StackDepth = 5;
            original.Channels.LCD.LineNumber = 45;
            original.Channels.AUX.VolumetricExtrusion = true;
            original.Channels.AutoPause.Compatibility = Compatibility.Marlin;

            MachineModel clone = (MachineModel)original.Clone();

            Assert.AreEqual(original.Channels.SPI.RelativePositioning, clone.Channels.SPI.RelativePositioning);
            Assert.AreEqual(original.Channels.USB.Feedrate, clone.Channels.USB.Feedrate);
            Assert.AreEqual(original.Channels.File.Feedrate, clone.Channels.File.Feedrate);
            Assert.AreEqual(original.Channels.CodeQueue.UsingInches, clone.Channels.CodeQueue.UsingInches);
            Assert.AreEqual(original.Channels.HTTP.RelativePositioning, clone.Channels.HTTP.RelativePositioning);
            Assert.AreEqual(original.Channels.Daemon.Feedrate, clone.Channels.Daemon.Feedrate);
            Assert.AreEqual(original.Channels.Telnet.StackDepth, clone.Channels.Telnet.StackDepth);
            Assert.AreEqual(original.Channels.LCD.LineNumber, clone.Channels.LCD.LineNumber);
            Assert.AreEqual(original.Channels.AUX.VolumetricExtrusion, clone.Channels.AUX.VolumetricExtrusion);
            Assert.AreEqual(original.Channels.AutoPause.Compatibility, clone.Channels.AutoPause.Compatibility);
        }

        [Test]
        public void Assign()
        {
            MachineModel original = new MachineModel();

            original.Channels.SPI.RelativePositioning = true;
            original.Channels.USB.Feedrate = 123F;
            original.Channels.File.Feedrate = 565F;
            original.Channels.CodeQueue.UsingInches = true;
            original.Channels.HTTP.RelativeExtrusion = true;
            original.Channels.Daemon.Feedrate = 45F;
            original.Channels[DuetAPI.CodeChannel.Telnet].StackDepth = 5;
            original.Channels.LCD.LineNumber = 45;
            original.Channels.AUX.VolumetricExtrusion = true;
            original.Channels.AutoPause.Compatibility = Compatibility.Marlin;

            MachineModel assigned = new MachineModel();
            assigned.Assign(original);

            Assert.AreEqual(original.Channels.SPI.RelativePositioning, assigned.Channels.SPI.RelativePositioning);
            Assert.AreEqual(original.Channels.USB.Feedrate, assigned.Channels.USB.Feedrate);
            Assert.AreEqual(original.Channels.File.Feedrate, assigned.Channels.File.Feedrate);
            Assert.AreEqual(original.Channels.CodeQueue.UsingInches, assigned.Channels.CodeQueue.UsingInches);
            Assert.AreEqual(original.Channels.HTTP.RelativePositioning, assigned.Channels.HTTP.RelativePositioning);
            Assert.AreEqual(original.Channels.Daemon.Feedrate, assigned.Channels.Daemon.Feedrate);
            Assert.AreEqual(original.Channels.Telnet.StackDepth, assigned.Channels.Telnet.StackDepth);
            Assert.AreEqual(original.Channels.LCD.LineNumber, assigned.Channels.LCD.LineNumber);
            Assert.AreEqual(original.Channels.AUX.VolumetricExtrusion, assigned.Channels.AUX.VolumetricExtrusion);
            Assert.AreEqual(original.Channels.AutoPause.Compatibility, assigned.Channels.AutoPause.Compatibility);
        }
    }
}
