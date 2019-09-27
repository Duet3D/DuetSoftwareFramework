using DuetAPI.Machine;
using NUnit.Framework;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class Storages
    {
        [Test]
        public void Clone()
        {
            MachineModel original = new MachineModel();

            Storage storage = new Storage
            {
                Capacity = 12345678,
                Free = 123456,
                Mounted = true,
                OpenFiles = 3467,
                Speed = 200000
            };
            original.Storages.Add(storage);

            MachineModel clone = (MachineModel)original.Clone();

            Assert.AreEqual(1, original.Storages.Count);
            Assert.AreEqual(original.Storages[0].Capacity, clone.Storages[0].Capacity);
            Assert.AreEqual(original.Storages[0].Free, clone.Storages[0].Free);
            Assert.AreEqual(original.Storages[0].Mounted, clone.Storages[0].Mounted);
            Assert.AreEqual(original.Storages[0].OpenFiles, clone.Storages[0].OpenFiles);
            Assert.AreEqual(original.Storages[0].Speed, clone.Storages[0].Speed);
        }

        [Test]
        public void Assign()
        {
            MachineModel original = new MachineModel();

            Storage storage = new Storage
            {
                Capacity = 12345678,
                Free = 123456,
                Mounted = true,
                OpenFiles = 3467,
                Speed = 200000
            };
            original.Storages.Add(storage);

            MachineModel assigned = new MachineModel();
            assigned.Assign(original);

            Assert.AreEqual(1, original.Storages.Count);
            Assert.AreEqual(original.Storages[0].Capacity, assigned.Storages[0].Capacity);
            Assert.AreEqual(original.Storages[0].Free, assigned.Storages[0].Free);
            Assert.AreEqual(original.Storages[0].Mounted, assigned.Storages[0].Mounted);
            Assert.AreEqual(original.Storages[0].OpenFiles, assigned.Storages[0].OpenFiles);
            Assert.AreEqual(original.Storages[0].Speed, assigned.Storages[0].Speed);
        }
    }
}
