using DuetAPI.Machine;
using NUnit.Framework;

namespace UnitTests.Machine
{
    [TestFixture]
    public class Scanner
    {
        [Test]
        public void Clone()
        {
            MachineModel original = new MachineModel();
            original.Scanner.Progress = 12.34F;
            original.Scanner.Status = ScannerStatus.PostProcessing;

            MachineModel clone = (MachineModel)original.Clone();

            Assert.AreEqual(original.Scanner.Progress, clone.Scanner.Progress);
            Assert.AreEqual(original.Scanner.Status, clone.Scanner.Status);

            Assert.AreNotSame(original.Scanner.Progress, clone.Scanner.Progress);
            Assert.AreNotSame(original.Scanner.Status, clone.Scanner.Status);
        }

        [Test]
        public void Assign()
        {
            MachineModel original = new MachineModel();
            original.Scanner.Progress = 12.34F;
            original.Scanner.Status = ScannerStatus.PostProcessing;

            MachineModel assigned = new MachineModel();
            assigned.Assign(original);

            Assert.AreEqual(original.Scanner.Progress, assigned.Scanner.Progress);
            Assert.AreEqual(original.Scanner.Status, assigned.Scanner.Status);

            Assert.AreNotSame(original.Scanner.Progress, assigned.Scanner.Progress);
            Assert.AreNotSame(original.Scanner.Status, assigned.Scanner.Status);
        }
    }
}
