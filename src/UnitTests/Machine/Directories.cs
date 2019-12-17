using DuetAPI.Machine;
using NUnit.Framework;

namespace UnitTests.Machine
{
    [TestFixture]
    public class Directories
    {
        [Test]
        public void Clone()
        {
            MachineModel original = new MachineModel();
            original.Directories.GCodes = "0:/gcodes/foo";
            original.Directories.Macros = "0:/my macros";
            original.Directories.System = "0:/sys/test";
            original.Directories.WWW = "0:/www/test";

            MachineModel clone = (MachineModel)original.Clone();

            Assert.AreEqual(original.Directories.GCodes, clone.Directories.GCodes);
            Assert.AreEqual(original.Directories.Macros, clone.Directories.Macros);
            Assert.AreEqual(original.Directories.System, clone.Directories.System);
            Assert.AreEqual(original.Directories.WWW, clone.Directories.WWW);
        }

        [Test]
        public void Assign()
        {
            MachineModel original = new MachineModel();
            original.Directories.GCodes = "0:/gcodes/foo";
            original.Directories.Macros = "0:/my macros";
            original.Directories.System = "0:/sys/test";
            original.Directories.WWW = "0:/www/test";

            MachineModel assigned = new MachineModel();
            assigned.Assign(original);

            Assert.AreEqual(original.Directories.GCodes, assigned.Directories.GCodes);
            Assert.AreEqual(original.Directories.Macros, assigned.Directories.Macros);
            Assert.AreEqual(original.Directories.System, assigned.Directories.System);
            Assert.AreEqual(original.Directories.WWW, assigned.Directories.WWW);
        }
    }
}
