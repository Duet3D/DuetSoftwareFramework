using NUnit.Framework;

namespace DuetUnitTest.File
{
    [TestFixture]
    [Platform("Linux,UNIX")]
    public class Path
    {
        [Test]
        public void Directory()
        {
            string sysPath = DuetControlServer.File.ResolvePath("0:/sys");
            Assert.AreEqual("/boot/sys", sysPath);
        }
    }
}
