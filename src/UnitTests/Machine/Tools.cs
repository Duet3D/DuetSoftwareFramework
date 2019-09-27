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
                Filament = "PET-G",
                Name = "Mixing Tool",
                Number = 3,
                Spindle = 3
            };
            tool.Active.Add(200F);
            tool.Active.Add(220F);
            tool.Fans.Add(3);
            tool.Heaters.Add(4);
            tool.Heaters.Add(5);
            tool.Mix.Add(0.4F);
            tool.Mix.Add(0.6F);
            tool.Offsets.Add(12F);
            tool.Offsets.Add(34F);
            tool.Offsets.Add(56F);
            tool.Standby.Add(40F);
            tool.Standby.Add(60F);
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
        }

        [Test]
        public void Assign()
        {
            MachineModel original = new MachineModel();

            Tool tool = new Tool
            {
                Filament = "PET-G",
                Name = "Mixing Tool",
                Number = 3,
                Spindle = 3
            };
            tool.Active.Add(200F);
            tool.Active.Add(220F);
            tool.Fans.Add(3);
            tool.Heaters.Add(4);
            tool.Heaters.Add(5);
            tool.Mix.Add(0.4F);
            tool.Mix.Add(0.6F);
            tool.Offsets.Add(12F);
            tool.Offsets.Add(34F);
            tool.Offsets.Add(56F);
            tool.Standby.Add(40F);
            tool.Standby.Add(60F);
            tool.Axes.Add(new int[] { 0 });
            tool.Axes.Add(new int[] { 1 });
            original.Tools.Add(tool);

            MachineModel assigned = new MachineModel();
            assigned.Assign(original);

            Assert.AreEqual(1, original.Tools.Count);
            Assert.AreEqual(original.Tools[0].Active, assigned.Tools[0].Active);
            Assert.AreEqual(original.Tools[0].Axes.Count, 2);
            Assert.AreEqual(original.Tools[0].Axes[0], assigned.Tools[0].Axes[0]);
            Assert.AreEqual(original.Tools[0].Axes[1], assigned.Tools[0].Axes[1]);
            Assert.AreEqual(original.Tools[0].Fans, assigned.Tools[0].Fans);
            Assert.AreEqual(original.Tools[0].Filament, assigned.Tools[0].Filament);
            Assert.AreEqual(original.Tools[0].Heaters, assigned.Tools[0].Heaters);
            Assert.AreEqual(original.Tools[0].Mix, assigned.Tools[0].Mix);
            Assert.AreEqual(original.Tools[0].Name, assigned.Tools[0].Name);
            Assert.AreEqual(original.Tools[0].Number, assigned.Tools[0].Number);
            Assert.AreEqual(original.Tools[0].Offsets, assigned.Tools[0].Offsets);
            Assert.AreEqual(original.Tools[0].Spindle, assigned.Tools[0].Spindle);
            Assert.AreEqual(original.Tools[0].Standby, assigned.Tools[0].Standby);
        }
    }
}
