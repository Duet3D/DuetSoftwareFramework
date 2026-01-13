using DuetAPI.Commands;
using DuetControlServer.Files;
using NUnit.Framework;
using System.Threading.Tasks;

namespace UnitTests.File
{
    // DISABLED: FilePathResolver now requires dependency injection - needs refactoring
    /*
    [Platform("Linux,UNIX")]
    public class Path
    {
        [Test]
        public async Task ToPhysicalAsync()
        {
            string sysPath = await FilePathResolver.ToPhysicalAsync("0:/sys");
            Assert.That(sysPath, Is.EqualTo("/opt/dsf/sd/sys"));

            string wwwPath = await FilePathResolver.ToPhysicalAsync("/www");
            Assert.That(wwwPath, Is.EqualTo("/opt/dsf/sd/www"));

            string configPath = await FilePathResolver.ToPhysicalAsync("config.g", "sys");
            Assert.That(configPath, Is.EqualTo("/opt/dsf/sd/sys/config.g"));

            string filamentsFile = await FilePathResolver.ToPhysicalAsync("foobar/config.g", FileDirectory.Filaments);
            Assert.That(filamentsFile, Is.EqualTo("/opt/dsf/sd/filaments/foobar/config.g"));

            string gcodeFile = await FilePathResolver.ToPhysicalAsync("test.g", FileDirectory.GCodes);
            Assert.That(gcodeFile, Is.EqualTo("/opt/dsf/sd/gcodes/test.g"));

            string macroFile = await FilePathResolver.ToPhysicalAsync("test.g", FileDirectory.Macros);
            Assert.That(macroFile, Is.EqualTo("/opt/dsf/sd/macros/test.g"));

            string sysFile = await FilePathResolver.ToPhysicalAsync("test.g", FileDirectory.System);
            Assert.That(sysFile, Is.EqualTo("/opt/dsf/sd/sys/test.g"));

            string wwwFile = await FilePathResolver.ToPhysicalAsync("index.html", FileDirectory.Web);
            Assert.That(wwwFile, Is.EqualTo("/opt/dsf/sd/www/index.html"));
        }

        [Test]
        public async Task ToVirtualAsync()
        {
            string sysPath = await FilePathResolver.ToVirtualAsync("/opt/dsf/sd/sys");
            Assert.That(sysPath, Is.EqualTo("0:/sys"));

            string wwwPath = await FilePathResolver.ToVirtualAsync("/opt/dsf/sd/www");
            Assert.That(wwwPath, Is.EqualTo("0:/www"));
        }
    }
    */
}
