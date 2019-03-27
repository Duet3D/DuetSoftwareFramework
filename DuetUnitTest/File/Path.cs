using DuetControlServer;
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
            string sysPath = FileHelper.ResolvePath("0:/sys");
            Assert.AreEqual("/opt/dsf/sd/sys", sysPath);
        }
    }
}
