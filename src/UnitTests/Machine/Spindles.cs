using DuetAPI.Machine;
using NUnit.Framework;

namespace UnitTests.Machine
{
    [TestFixture]
    public class Spindles
    {
        [Test]
        public void Clone()
        {
            MachineModel original = new MachineModel();

            Spindle spindle = new Spindle
            {
                Active = 123.45F,
                Current = 45.678F
            };
            original.Spindles.Add(spindle);

            MachineModel clone = (MachineModel)original.Clone();

            Assert.AreEqual(1, original.Spindles.Count);
            Assert.AreEqual(original.Spindles[0].Active, clone.Spindles[0].Active);
            Assert.AreEqual(original.Spindles[0].Current, clone.Spindles[0].Current);

            Assert.AreNotSame(original.Spindles[0].Active, clone.Spindles[0].Active);
            Assert.AreNotSame(original.Spindles[0].Current, clone.Spindles[0].Current);
        }

        [Test]
        public void Assign()
        {
            MachineModel original = new MachineModel();

            Spindle spindle = new Spindle
            {
                Active = 123.45F,
                Current = 45.678F
            };
            original.Spindles.Add(spindle);

            MachineModel assigned = new MachineModel();
            assigned.Assign(original);

            Assert.AreEqual(1, original.Spindles.Count);
            Assert.AreEqual(original.Spindles[0].Active, assigned.Spindles[0].Active);
            Assert.AreEqual(original.Spindles[0].Current, assigned.Spindles[0].Current);

            Assert.AreNotSame(original.Spindles[0].Active, assigned.Spindles[0].Active);
            Assert.AreNotSame(original.Spindles[0].Current, assigned.Spindles[0].Current);
        }
    }
}
