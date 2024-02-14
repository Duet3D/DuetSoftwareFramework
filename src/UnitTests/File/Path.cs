using DuetControlServer.Files;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using System.Threading.Tasks;

namespace UnitTests.File
{
    [TestFixture]
    [Platform("Linux,UNIX")]
    public class Path
    {
        [Test]
        public async Task ToPhysicalAsync()
        {
            string sysPath = await FilePath.ToPhysicalAsync("0:/sys");
            ClassicAssert.AreEqual("/opt/dsf/sd/sys", sysPath);

            string wwwPath = await FilePath.ToPhysicalAsync("/www");
            ClassicAssert.AreEqual("/opt/dsf/sd/www", wwwPath);

            string configPath = await FilePath.ToPhysicalAsync("config.g", "sys");
            ClassicAssert.AreEqual("/opt/dsf/sd/sys/config.g", configPath);

            string filamentsFile = await FilePath.ToPhysicalAsync("foobar/config.g", FileDirectory.Filaments);
            ClassicAssert.AreEqual("/opt/dsf/sd/filaments/foobar/config.g", filamentsFile);

            string gcodeFile = await FilePath.ToPhysicalAsync("test.g", FileDirectory.GCodes);
            ClassicAssert.AreEqual("/opt/dsf/sd/gcodes/test.g", gcodeFile);

            string macroFile = await FilePath.ToPhysicalAsync("test.g", FileDirectory.Macros);
            ClassicAssert.AreEqual("/opt/dsf/sd/macros/test.g", macroFile);

            string sysFile = await FilePath.ToPhysicalAsync("test.g", FileDirectory.System);
            ClassicAssert.AreEqual("/opt/dsf/sd/sys/test.g", sysFile);

            string wwwFile = await FilePath.ToPhysicalAsync("index.html", FileDirectory.Web);
            ClassicAssert.AreEqual("/opt/dsf/sd/www/index.html", wwwFile);
        }

        [Test]
        public async Task ToVirtualAsync()
        {
            string sysPath = await FilePath.ToVirtualAsync("/opt/dsf/sd/sys");
            ClassicAssert.AreEqual("0:/sys", sysPath);

            string wwwPath = await FilePath.ToVirtualAsync("/opt/dsf/sd/www");
            ClassicAssert.AreEqual("0:/www", wwwPath);
        }
    }
}
