using DuetControlServer;
using NUnit.Framework;

namespace DuetUnitTest.File
{
    [TestFixture]
    [Platform("Linux,UNIX")]
    public class Path
    {
        [Test]
        public void ToPhysical()
        {
            string sysPath = FilePath.ToPhysical("0:/sys").Result;
            Assert.AreEqual("/opt/dsf/sd/sys", sysPath);

            string wwwPath = FilePath.ToPhysical("/www").Result;
            Assert.AreEqual("/opt/dsf/sd/www", wwwPath);

            string configPath = FilePath.ToPhysical("config.g", "sys").Result;
            Assert.AreEqual("/opt/dsf/sd/sys/config.g", configPath);
        }

        [Test]
        public void ToVirtual()
        {
            string sysPath = FilePath.ToVirtual("/opt/dsf/sd/sys").Result;
            Assert.AreEqual("0:/sys", sysPath);
        }
    }
}
