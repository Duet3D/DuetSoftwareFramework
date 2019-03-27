using DuetAPI.Machine;
using DuetAPI.Machine.Spindles;
using NUnit.Framework;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class Spindles
    {
        [Test]
        public void Clone()
        {
            Model original = new Model();

            Spindle spindle = new Spindle
            {
                Active = 123.45,
                Current = 45.678
            };
            original.Spindles.Add(spindle);

            Model clone = (Model)original.Clone();

            Assert.AreEqual(1, original.Spindles.Count);
            Assert.AreEqual(original.Spindles[0].Active, clone.Spindles[0].Active);
            Assert.AreEqual(original.Spindles[0].Current, clone.Spindles[0].Current);

            Assert.AreNotSame(original.Spindles[0].Active, clone.Spindles[0].Active);
            Assert.AreNotSame(original.Spindles[0].Current, clone.Spindles[0].Current);
        }
    }
}
