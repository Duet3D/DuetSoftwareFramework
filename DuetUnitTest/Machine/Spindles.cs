using DuetAPI.Machine.Spindles;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Text;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class Spindles
    {
        [Test]
        public void Clone()
        {
            DuetAPI.Machine.Model original = new DuetAPI.Machine.Model();

            Spindle spindle = new Spindle
            {
                Active = 123.45,
                Current = 45.678
            };
            original.Spindles.Add(spindle);

            DuetAPI.Machine.Model clone = (DuetAPI.Machine.Model)original.Clone();

            Assert.AreEqual(1, original.Spindles.Count);
            Assert.AreEqual(original.Spindles[0].Active, clone.Spindles[0].Active);
            Assert.AreEqual(original.Spindles[0].Current, clone.Spindles[0].Current);

            Assert.AreNotSame(original.Spindles[0].Active, clone.Spindles[0].Active);
            Assert.AreNotSame(original.Spindles[0].Current, clone.Spindles[0].Current);
        }
    }
}
