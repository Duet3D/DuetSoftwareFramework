using DuetAPI.Machine;
using NUnit.Framework;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class Fans
    {
        [Test]
        public void Clone()
        {
            MachineModel original = new MachineModel();

            Fan fan = new Fan
            {
                Value = 100,
                Name = "Fan Name",
                Rpm = 1234,
                Inverted = true,
                Frequency = 20000,
                Min = 0.01F,
                Max = 0.99F,
                Blip = 0
            };
            fan.Thermostatic.Control = false;
            fan.Thermostatic.Heaters.Add(1);
            fan.Thermostatic.Heaters.Add(2);
            fan.Thermostatic.Temperature = 79F;
            fan.Pin = 23;
            original.Fans.Add(fan);

            MachineModel clone = (MachineModel)original.Clone();

            Assert.AreEqual(1, original.Fans.Count);
            Assert.AreEqual(original.Fans[0].Value, clone.Fans[0].Value);
            Assert.AreEqual(original.Fans[0].Name, clone.Fans[0].Name);
            Assert.AreEqual(original.Fans[0].Rpm, clone.Fans[0].Rpm);
            Assert.AreEqual(original.Fans[0].Inverted, clone.Fans[0].Inverted);
            Assert.AreEqual(original.Fans[0].Frequency, clone.Fans[0].Frequency);
            Assert.AreEqual(original.Fans[0].Min, clone.Fans[0].Min);
            Assert.AreEqual(original.Fans[0].Max, clone.Fans[0].Max);
            Assert.AreEqual(original.Fans[0].Blip, clone.Fans[0].Blip);
            Assert.AreEqual(original.Fans[0].Thermostatic.Control, clone.Fans[0].Thermostatic.Control);
            Assert.AreEqual(original.Fans[0].Thermostatic.Heaters, clone.Fans[0].Thermostatic.Heaters);
            Assert.AreEqual(original.Fans[0].Thermostatic.Temperature, clone.Fans[0].Thermostatic.Temperature);
            Assert.AreEqual(original.Fans[0].Pin, clone.Fans[0].Pin);
        }

        [Test]
        public void Assign()
        {
            MachineModel original = new MachineModel();

            Fan fan = new Fan
            {
                Value = 100,
                Name = "Fan Name",
                Rpm = 1234,
                Inverted = true,
                Frequency = 20000,
                Min = 0.01F,
                Max = 0.99F,
                Blip = 0
            };
            fan.Thermostatic.Control = false;
            fan.Thermostatic.Heaters.Add(1);
            fan.Thermostatic.Heaters.Add(2);
            fan.Thermostatic.Temperature = 79F;
            fan.Pin = 23;
            original.Fans.Add(fan);

            MachineModel assigned = new MachineModel();
            assigned.Assign(original);

            Assert.AreEqual(1, original.Fans.Count);
            Assert.AreEqual(original.Fans[0].Value, assigned.Fans[0].Value);
            Assert.AreEqual(original.Fans[0].Name, assigned.Fans[0].Name);
            Assert.AreEqual(original.Fans[0].Rpm, assigned.Fans[0].Rpm);
            Assert.AreEqual(original.Fans[0].Inverted, assigned.Fans[0].Inverted);
            Assert.AreEqual(original.Fans[0].Frequency, assigned.Fans[0].Frequency);
            Assert.AreEqual(original.Fans[0].Min, assigned.Fans[0].Min);
            Assert.AreEqual(original.Fans[0].Max, assigned.Fans[0].Max);
            Assert.AreEqual(original.Fans[0].Blip, assigned.Fans[0].Blip);
            Assert.AreEqual(original.Fans[0].Thermostatic.Control, assigned.Fans[0].Thermostatic.Control);
            Assert.AreEqual(original.Fans[0].Thermostatic.Heaters, assigned.Fans[0].Thermostatic.Heaters);
            Assert.AreEqual(original.Fans[0].Thermostatic.Temperature, assigned.Fans[0].Thermostatic.Temperature);
            Assert.AreEqual(original.Fans[0].Pin, assigned.Fans[0].Pin);
        }
    }
}
