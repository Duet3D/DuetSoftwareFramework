using DuetAPI.Machine;
using DuetAPI.Machine.Fans;
using NUnit.Framework;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class Fans
    {
        [Test]
        public void Clone()
        {
            Model original = new Model();

            Fan fan = new Fan();
            fan.Value = 100;
            fan.Name = "Fan Name";
            fan.Rpm = 1234;
            fan.Inverted = true;
            fan.Frequency = 20000;
            fan.Min = 0.01;
            fan.Max = 0.99;
            fan.Blip = 0;
            fan.Thermostatic.Control = false;
            fan.Thermostatic.Heaters = new uint[] { 1, 2 };
            fan.Thermostatic.Temperature = 79;
            fan.Pin = 23;
            original.Fans.Add(fan);

            Model clone = (Model)original.Clone();

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

            Assert.AreNotSame(original.Fans[0].Value, clone.Fans[0].Value);
            Assert.AreNotSame(original.Fans[0].Name, clone.Fans[0].Name);
            Assert.AreNotSame(original.Fans[0].Rpm, clone.Fans[0].Rpm);
            Assert.AreNotSame(original.Fans[0].Inverted, clone.Fans[0].Inverted);
            Assert.AreNotSame(original.Fans[0].Frequency, clone.Fans[0].Frequency);
            Assert.AreNotSame(original.Fans[0].Min, clone.Fans[0].Min);
            Assert.AreNotSame(original.Fans[0].Max, clone.Fans[0].Max);
            Assert.AreNotSame(original.Fans[0].Blip, clone.Fans[0].Blip);
            Assert.AreNotSame(original.Fans[0].Thermostatic.Control, clone.Fans[0].Thermostatic.Control);
            Assert.AreNotSame(original.Fans[0].Thermostatic.Heaters, clone.Fans[0].Thermostatic.Heaters);
            Assert.AreNotSame(original.Fans[0].Thermostatic.Temperature, clone.Fans[0].Thermostatic.Temperature);
            Assert.AreNotSame(original.Fans[0].Pin, clone.Fans[0].Pin);
        }
    }
}
