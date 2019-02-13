using DuetAPI.Machine.Storages;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Text;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class Storages
    {
        [Test]
        public void Clone()
        {
            DuetAPI.Machine.Model original = new DuetAPI.Machine.Model();

            Storage storage = new Storage
            {
                Capacity = 12345678,
                Free = 123456,
                Mounted = true,
                OpenFiles = 3467,
                Speed = 200000
            };
            original.Storages.Add(storage);

            DuetAPI.Machine.Model clone = (DuetAPI.Machine.Model)original.Clone();

            Assert.AreEqual(1, original.Storages.Count);
            Assert.AreEqual(original.Storages[0].Capacity, clone.Storages[0].Capacity);
            Assert.AreEqual(original.Storages[0].Free, clone.Storages[0].Free);
            Assert.AreEqual(original.Storages[0].Mounted, clone.Storages[0].Mounted);
            Assert.AreEqual(original.Storages[0].OpenFiles, clone.Storages[0].OpenFiles);
            Assert.AreEqual(original.Storages[0].Speed, clone.Storages[0].Speed);

            Assert.AreNotSame(original.Storages[0].Capacity, clone.Storages[0].Capacity);
            Assert.AreNotSame(original.Storages[0].Free, clone.Storages[0].Free);
            Assert.AreNotSame(original.Storages[0].Mounted, clone.Storages[0].Mounted);
            Assert.AreNotSame(original.Storages[0].OpenFiles, clone.Storages[0].OpenFiles);
            Assert.AreNotSame(original.Storages[0].Speed, clone.Storages[0].Speed);
        }
    }
}
