using DuetAPI.Machine;
using NUnit.Framework;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class Scanner
    {
        [Test]
        public void Clone()
        {
            MachineModel original = new MachineModel();
            original.Scanner.Progress = 12.34;
            original.Scanner.Status = ScannerStatus.PostProcessing;

            MachineModel clone = (MachineModel)original.Clone();

            Assert.AreEqual(original.Scanner.Progress, clone.Scanner.Progress);
            Assert.AreEqual(original.Scanner.Status, clone.Scanner.Status);

            Assert.AreNotSame(original.Scanner.Progress, clone.Scanner.Progress);
            Assert.AreNotSame(original.Scanner.Status, clone.Scanner.Status);
        }
    }
}
