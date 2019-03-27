using DuetAPI.Machine.Scanner;
using NUnit.Framework;
using Model = DuetAPI.Machine.Model;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class Scanner
    {
        [Test]
        public void Clone()
        {
            Model original = new Model();
            original.Scanner.Progress = 12.34;
            original.Scanner.Status = ScannerStatus.PostProcessing;

            Model clone = (Model)original.Clone();

            Assert.AreEqual(original.Scanner.Progress, clone.Scanner.Progress);
            Assert.AreEqual(original.Scanner.Status, clone.Scanner.Status);

            Assert.AreNotSame(original.Scanner.Progress, clone.Scanner.Progress);
            Assert.AreNotSame(original.Scanner.Status, clone.Scanner.Status);
        }
    }
}
