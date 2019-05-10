using DuetAPI.Machine;
using NUnit.Framework;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class Tools
    {
        [Test]
        public void Clone()
        {
            MachineModel original = new MachineModel();

            Tool tool = new Tool
            {
                Active = new float[] { 200F, 220F },
                Fans = new int[] { 3 },
                Filament = "PET-G",
                Heaters = new int[] { 4, 5 },
                Mix = new float[] { 0.4F, 0.6F },
                Name = "Mixing Tool",
                Number = 3,
                Offsets = new float[] { 12F, 34F, 56F },
                Spindle = 3,
                Standby = new float[] { 40F, 60F }
            };
            tool.Axes.Add(new int[] { 0 });
            tool.Axes.Add(new int[] { 1 });
            original.Tools.Add(tool);

            MachineModel clone = (MachineModel)original.Clone();

            Assert.AreEqual(1, original.Tools.Count);
            Assert.AreEqual(original.Tools[0].Active, clone.Tools[0].Active);
            Assert.AreEqual(original.Tools[0].Axes.Count, 2);
            Assert.AreEqual(original.Tools[0].Axes[0], clone.Tools[0].Axes[0]);
            Assert.AreEqual(original.Tools[0].Axes[1], clone.Tools[0].Axes[1]);
            Assert.AreEqual(original.Tools[0].Fans, clone.Tools[0].Fans);
            Assert.AreEqual(original.Tools[0].Filament, clone.Tools[0].Filament);
            Assert.AreEqual(original.Tools[0].Heaters, clone.Tools[0].Heaters);
            Assert.AreEqual(original.Tools[0].Mix, clone.Tools[0].Mix);
            Assert.AreEqual(original.Tools[0].Name, clone.Tools[0].Name);
            Assert.AreEqual(original.Tools[0].Number, clone.Tools[0].Number);
            Assert.AreEqual(original.Tools[0].Offsets, clone.Tools[0].Offsets);
            Assert.AreEqual(original.Tools[0].Spindle, clone.Tools[0].Spindle);
            Assert.AreEqual(original.Tools[0].Standby, clone.Tools[0].Standby);

            Assert.AreNotSame(original.Tools[0].Active, clone.Tools[0].Active);
            Assert.AreNotSame(original.Tools[0].Axes, clone.Tools[0].Axes);
            Assert.AreNotSame(original.Tools[0].Axes[0], clone.Tools[0].Axes[0]);
            Assert.AreNotSame(original.Tools[0].Axes[1], clone.Tools[0].Axes[1]);
            Assert.AreNotSame(original.Tools[0].Fans, clone.Tools[0].Fans);
            Assert.AreNotSame(original.Tools[0].Filament, clone.Tools[0].Filament);
            Assert.AreNotSame(original.Tools[0].Heaters, clone.Tools[0].Heaters);
            Assert.AreNotSame(original.Tools[0].Mix, clone.Tools[0].Mix);
            Assert.AreNotSame(original.Tools[0].Name, clone.Tools[0].Name);
            Assert.AreNotSame(original.Tools[0].Number, clone.Tools[0].Number);
            Assert.AreNotSame(original.Tools[0].Offsets, clone.Tools[0].Offsets);
            Assert.AreNotSame(original.Tools[0].Spindle, clone.Tools[0].Spindle);
            Assert.AreNotSame(original.Tools[0].Standby, clone.Tools[0].Standby);
        }
    }
}
