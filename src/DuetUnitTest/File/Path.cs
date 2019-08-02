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
            string sysPath = FilePath.ToPhysical("0:/sys");
            Assert.AreEqual("/opt/dsf/sd/sys", sysPath);

            string wwwPath = FilePath.ToPhysical("/www");
            Assert.AreEqual("/opt/dsf/sd/www", wwwPath);

            string configPath = FilePath.ToPhysicalAsync("config.g", "sys").Result;
            Assert.AreEqual("/opt/dsf/sd/sys/config.g", configPath);
        }

        [Test]
        public void ToVirtual()
        {
            string sysPath = FilePath.ToVirtual("/opt/dsf/sd/sys");
            Assert.AreEqual("0:/sys", sysPath);

            string wwwPath = FilePath.ToVirtualAsync("/opt/dsf/sd/www").Result;
            Assert.AreEqual("0:/www", wwwPath);
        }
    }
}
