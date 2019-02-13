using NUnit.Framework;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class Scanner
    {
        [Test]
        public void Clone()
        {
            DuetAPI.Machine.Model original = new DuetAPI.Machine.Model();
            original.Scanner.Progress = 12.34;
            original.Scanner.Status = DuetAPI.Machine.Scanner.ScannerStatus.PostProcessing;

            DuetAPI.Machine.Model clone = (DuetAPI.Machine.Model)original;

            Assert.AreEqual(original.Scanner.Progress, clone.Scanner.Progress);
            Assert.AreEqual(original.Scanner.Status, clone.Scanner.Status);

            Assert.AreNotSame(original.Scanner.Progress, clone.Scanner.Progress);
            Assert.AreNotSame(original.Scanner.Status, clone.Scanner.Status);
        }
    }
}
