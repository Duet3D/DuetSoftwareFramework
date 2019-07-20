using DuetAPI.Machine;
using NUnit.Framework;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class Electronics
    {
        [Test]
        public void Clone()
        {
            MachineModel original = new MachineModel();
            original.Electronics.Type = "Electronics Type";
            original.Electronics.Name = "Electronics Name";
            original.Electronics.Revision = "Electronics Revision";
            original.Electronics.Firmware.Name = "Firmware Name";
            original.Electronics.Firmware.Version = "Firmware Version";
            original.Electronics.Firmware.Date = "Firmware Date";
            original.Electronics.ProcessorID = "Processor ID";
            original.Electronics.VIn.Current = 321;
            original.Electronics.VIn.Min = 654F;
            original.Electronics.VIn.Max = 987F;
            original.Electronics.McuTemp.Current = 123F;
            original.Electronics.McuTemp.Min = 456F;
            original.Electronics.McuTemp.Max = 789F;

            ExpansionBoard expansionBoard = new ExpansionBoard
            {
                Name = "Expansion Name",
                Revision = "Expansion Revision"
            };
            expansionBoard.Firmware.Name = "Expansion Firmware Name";
            expansionBoard.Firmware.Date = "Expansion Firmware Date";
            expansionBoard.Firmware.Version = "Expansion Firmware Version";
            expansionBoard.VIn.Current = 321F;
            expansionBoard.VIn.Min = 654F;
            expansionBoard.VIn.Max = 987F;
            expansionBoard.McuTemp.Current = 123F;
            expansionBoard.McuTemp.Min = 456F;
            expansionBoard.McuTemp.Max = 789F;
            expansionBoard.MaxHeaters = 12;
            expansionBoard.MaxMotors = 6;
            original.Electronics.ExpansionBoards.Add(expansionBoard);

            MachineModel clone = (MachineModel)original.Clone();

            Assert.AreEqual(original.Electronics.Type, clone.Electronics.Type);
            Assert.AreEqual(original.Electronics.Name, clone.Electronics.Name);
            Assert.AreEqual(original.Electronics.Revision, clone.Electronics.Revision);
            Assert.AreEqual(original.Electronics.Firmware.Name, clone.Electronics.Firmware.Name);
            Assert.AreEqual(original.Electronics.Firmware.Version, clone.Electronics.Firmware.Version);
            Assert.AreEqual(original.Electronics.Firmware.Date, clone.Electronics.Firmware.Date);
            Assert.AreEqual(original.Electronics.ProcessorID, clone.Electronics.ProcessorID);
            Assert.AreEqual(original.Electronics.VIn.Current, clone.Electronics.VIn.Current);
            Assert.AreEqual(original.Electronics.VIn.Min, clone.Electronics.VIn.Min);
            Assert.AreEqual(original.Electronics.VIn.Max, clone.Electronics.VIn.Max);
            Assert.AreEqual(original.Electronics.McuTemp.Current, clone.Electronics.McuTemp.Current);
            Assert.AreEqual(original.Electronics.McuTemp.Min, clone.Electronics.McuTemp.Min);
            Assert.AreEqual(original.Electronics.McuTemp.Max, clone.Electronics.McuTemp.Max);

            Assert.AreEqual(1, clone.Electronics.ExpansionBoards.Count);
            Assert.AreEqual(original.Electronics.ExpansionBoards[0].Name, clone.Electronics.ExpansionBoards[0].Name);
            Assert.AreEqual(original.Electronics.ExpansionBoards[0].Revision, clone.Electronics.ExpansionBoards[0].Revision);
            Assert.AreEqual(original.Electronics.ExpansionBoards[0].Firmware.Name, clone.Electronics.ExpansionBoards[0].Firmware.Name);
            Assert.AreEqual(original.Electronics.ExpansionBoards[0].Firmware.Date, clone.Electronics.ExpansionBoards[0].Firmware.Date);
            Assert.AreEqual(original.Electronics.ExpansionBoards[0].Firmware.Version, clone.Electronics.ExpansionBoards[0].Firmware.Version);
            Assert.AreEqual(original.Electronics.ExpansionBoards[0].VIn.Current, clone.Electronics.ExpansionBoards[0].VIn.Current);
            Assert.AreEqual(original.Electronics.ExpansionBoards[0].VIn.Min, clone.Electronics.ExpansionBoards[0].VIn.Min);
            Assert.AreEqual(original.Electronics.ExpansionBoards[0].VIn.Max, clone.Electronics.ExpansionBoards[0].VIn.Max);
            Assert.AreEqual(original.Electronics.ExpansionBoards[0].McuTemp.Current, clone.Electronics.ExpansionBoards[0].McuTemp.Current);
            Assert.AreEqual(original.Electronics.ExpansionBoards[0].McuTemp.Min, clone.Electronics.ExpansionBoards[0].McuTemp.Min);
            Assert.AreEqual(original.Electronics.ExpansionBoards[0].McuTemp.Max, clone.Electronics.ExpansionBoards[0].McuTemp.Max);
            Assert.AreEqual(original.Electronics.ExpansionBoards[0].MaxHeaters, clone.Electronics.ExpansionBoards[0].MaxHeaters);
            Assert.AreEqual(original.Electronics.ExpansionBoards[0].MaxMotors, clone.Electronics.ExpansionBoards[0].MaxMotors);

            Assert.AreNotSame(original.Electronics.Type, clone.Electronics.Type);
            Assert.AreNotSame(original.Electronics.Name, clone.Electronics.Name);
            Assert.AreNotSame(original.Electronics.Revision, clone.Electronics.Revision);
            Assert.AreNotSame(original.Electronics.Firmware.Name, clone.Electronics.Firmware.Name);
            Assert.AreNotSame(original.Electronics.Firmware.Version, clone.Electronics.Firmware.Version);
            Assert.AreNotSame(original.Electronics.Firmware.Date, clone.Electronics.Firmware.Date);
            Assert.AreNotSame(original.Electronics.ProcessorID, clone.Electronics.ProcessorID);
            Assert.AreNotSame(original.Electronics.VIn.Current, clone.Electronics.VIn.Current);
            Assert.AreNotSame(original.Electronics.VIn.Min, clone.Electronics.VIn.Min);
            Assert.AreNotSame(original.Electronics.VIn.Max, clone.Electronics.VIn.Max);
            Assert.AreNotSame(original.Electronics.McuTemp.Current, clone.Electronics.McuTemp.Current);
            Assert.AreNotSame(original.Electronics.McuTemp.Min, clone.Electronics.McuTemp.Min);
            Assert.AreNotSame(original.Electronics.McuTemp.Max, clone.Electronics.McuTemp.Max);

            Assert.AreNotSame(original.Electronics.ExpansionBoards[0].Name, clone.Electronics.ExpansionBoards[0].Name);
            Assert.AreNotSame(original.Electronics.ExpansionBoards[0].Revision, clone.Electronics.ExpansionBoards[0].Revision);
            Assert.AreNotSame(original.Electronics.ExpansionBoards[0].Firmware.Name, clone.Electronics.ExpansionBoards[0].Firmware.Name);
            Assert.AreNotSame(original.Electronics.ExpansionBoards[0].Firmware.Date, clone.Electronics.ExpansionBoards[0].Firmware.Date);
            Assert.AreNotSame(original.Electronics.ExpansionBoards[0].Firmware.Version, clone.Electronics.ExpansionBoards[0].Firmware.Version);
            Assert.AreNotSame(original.Electronics.ExpansionBoards[0].VIn.Current, clone.Electronics.ExpansionBoards[0].VIn.Current);
            Assert.AreNotSame(original.Electronics.ExpansionBoards[0].VIn.Min, clone.Electronics.ExpansionBoards[0].VIn.Min);
            Assert.AreNotSame(original.Electronics.ExpansionBoards[0].VIn.Max, clone.Electronics.ExpansionBoards[0].VIn.Max);
            Assert.AreNotSame(original.Electronics.ExpansionBoards[0].McuTemp.Current, clone.Electronics.ExpansionBoards[0].McuTemp.Current);
            Assert.AreNotSame(original.Electronics.ExpansionBoards[0].McuTemp.Min, clone.Electronics.ExpansionBoards[0].McuTemp.Min);
            Assert.AreNotSame(original.Electronics.ExpansionBoards[0].McuTemp.Max, clone.Electronics.ExpansionBoards[0].McuTemp.Max);
            Assert.AreNotSame(original.Electronics.ExpansionBoards[0].MaxHeaters, clone.Electronics.ExpansionBoards[0].MaxHeaters);
            Assert.AreNotSame(original.Electronics.ExpansionBoards[0].MaxMotors, clone.Electronics.ExpansionBoards[0].MaxMotors);
        }

        [Test]
        public void Assign()
        {
            MachineModel original = new MachineModel();
            original.Electronics.Type = "Electronics Type";
            original.Electronics.Name = "Electronics Name";
            original.Electronics.Revision = "Electronics Revision";
            original.Electronics.Firmware.Name = "Firmware Name";
            original.Electronics.Firmware.Version = "Firmware Version";
            original.Electronics.Firmware.Date = "Firmware Date";
            original.Electronics.ProcessorID = "Processor ID";
            original.Electronics.VIn.Current = 321F;
            original.Electronics.VIn.Min = 654F;
            original.Electronics.VIn.Max = 987F;
            original.Electronics.McuTemp.Current = 123F;
            original.Electronics.McuTemp.Min = 456F;
            original.Electronics.McuTemp.Max = 789F;

            ExpansionBoard expansionBoard = new ExpansionBoard
            {
                Name = "Expansion Name",
                Revision = "Expansion Revision",
                MaxHeaters = 12,
                MaxMotors = 6
            };
            expansionBoard.Firmware.Name = "Expansion Firmware Name";
            expansionBoard.Firmware.Date = "Expansion Firmware Date";
            expansionBoard.Firmware.Version = "Expansion Firmware Version";
            expansionBoard.VIn.Current = 321F;
            expansionBoard.VIn.Min = 654F;
            expansionBoard.VIn.Max = 987F;
            expansionBoard.McuTemp.Current = 123F;
            expansionBoard.McuTemp.Min = 456F;
            expansionBoard.McuTemp.Max = 789F;
            expansionBoard.MaxHeaters = 12;
            expansionBoard.MaxMotors = 6;
            original.Electronics.ExpansionBoards.Add(expansionBoard);

            MachineModel assigned = new MachineModel();
            assigned.Assign(original);

            Assert.AreEqual(original.Electronics.Type, assigned.Electronics.Type);
            Assert.AreEqual(original.Electronics.Name, assigned.Electronics.Name);
            Assert.AreEqual(original.Electronics.Revision, assigned.Electronics.Revision);
            Assert.AreEqual(original.Electronics.Firmware.Name, assigned.Electronics.Firmware.Name);
            Assert.AreEqual(original.Electronics.Firmware.Version, assigned.Electronics.Firmware.Version);
            Assert.AreEqual(original.Electronics.Firmware.Date, assigned.Electronics.Firmware.Date);
            Assert.AreEqual(original.Electronics.ProcessorID, assigned.Electronics.ProcessorID);
            Assert.AreEqual(original.Electronics.VIn.Current, assigned.Electronics.VIn.Current);
            Assert.AreEqual(original.Electronics.VIn.Min, assigned.Electronics.VIn.Min);
            Assert.AreEqual(original.Electronics.VIn.Max, assigned.Electronics.VIn.Max);
            Assert.AreEqual(original.Electronics.McuTemp.Current, assigned.Electronics.McuTemp.Current);
            Assert.AreEqual(original.Electronics.McuTemp.Min, assigned.Electronics.McuTemp.Min);
            Assert.AreEqual(original.Electronics.McuTemp.Max, assigned.Electronics.McuTemp.Max);

            Assert.AreEqual(1, assigned.Electronics.ExpansionBoards.Count);
            Assert.AreEqual(original.Electronics.ExpansionBoards[0].Name, assigned.Electronics.ExpansionBoards[0].Name);
            Assert.AreEqual(original.Electronics.ExpansionBoards[0].Revision, assigned.Electronics.ExpansionBoards[0].Revision);
            Assert.AreEqual(original.Electronics.ExpansionBoards[0].Firmware.Name, assigned.Electronics.ExpansionBoards[0].Firmware.Name);
            Assert.AreEqual(original.Electronics.ExpansionBoards[0].Firmware.Date, assigned.Electronics.ExpansionBoards[0].Firmware.Date);
            Assert.AreEqual(original.Electronics.ExpansionBoards[0].Firmware.Version, assigned.Electronics.ExpansionBoards[0].Firmware.Version);
            Assert.AreEqual(original.Electronics.ExpansionBoards[0].VIn.Current, assigned.Electronics.ExpansionBoards[0].VIn.Current);
            Assert.AreEqual(original.Electronics.ExpansionBoards[0].VIn.Min, assigned.Electronics.ExpansionBoards[0].VIn.Min);
            Assert.AreEqual(original.Electronics.ExpansionBoards[0].VIn.Max, assigned.Electronics.ExpansionBoards[0].VIn.Max);
            Assert.AreEqual(original.Electronics.ExpansionBoards[0].McuTemp.Current, assigned.Electronics.ExpansionBoards[0].McuTemp.Current);
            Assert.AreEqual(original.Electronics.ExpansionBoards[0].McuTemp.Min, assigned.Electronics.ExpansionBoards[0].McuTemp.Min);
            Assert.AreEqual(original.Electronics.ExpansionBoards[0].McuTemp.Max, assigned.Electronics.ExpansionBoards[0].McuTemp.Max);
            Assert.AreEqual(original.Electronics.ExpansionBoards[0].MaxHeaters, assigned.Electronics.ExpansionBoards[0].MaxHeaters);
            Assert.AreEqual(original.Electronics.ExpansionBoards[0].MaxMotors, assigned.Electronics.ExpansionBoards[0].MaxMotors);

            Assert.AreNotSame(original.Electronics.Type, assigned.Electronics.Type);
            Assert.AreNotSame(original.Electronics.Name, assigned.Electronics.Name);
            Assert.AreNotSame(original.Electronics.Revision, assigned.Electronics.Revision);
            Assert.AreNotSame(original.Electronics.Firmware.Name, assigned.Electronics.Firmware.Name);
            Assert.AreNotSame(original.Electronics.Firmware.Version, assigned.Electronics.Firmware.Version);
            Assert.AreNotSame(original.Electronics.Firmware.Date, assigned.Electronics.Firmware.Date);
            Assert.AreNotSame(original.Electronics.ProcessorID, assigned.Electronics.ProcessorID);
            Assert.AreNotSame(original.Electronics.VIn.Current, assigned.Electronics.VIn.Current);
            Assert.AreNotSame(original.Electronics.VIn.Min, assigned.Electronics.VIn.Min);
            Assert.AreNotSame(original.Electronics.VIn.Max, assigned.Electronics.VIn.Max);
            Assert.AreNotSame(original.Electronics.McuTemp.Current, assigned.Electronics.McuTemp.Current);
            Assert.AreNotSame(original.Electronics.McuTemp.Min, assigned.Electronics.McuTemp.Min);
            Assert.AreNotSame(original.Electronics.McuTemp.Max, assigned.Electronics.McuTemp.Max);

            Assert.AreNotSame(original.Electronics.ExpansionBoards[0].Name, assigned.Electronics.ExpansionBoards[0].Name);
            Assert.AreNotSame(original.Electronics.ExpansionBoards[0].Revision, assigned.Electronics.ExpansionBoards[0].Revision);
            Assert.AreNotSame(original.Electronics.ExpansionBoards[0].Firmware.Name, assigned.Electronics.ExpansionBoards[0].Firmware.Name);
            Assert.AreNotSame(original.Electronics.ExpansionBoards[0].Firmware.Date, assigned.Electronics.ExpansionBoards[0].Firmware.Date);
            Assert.AreNotSame(original.Electronics.ExpansionBoards[0].Firmware.Version, assigned.Electronics.ExpansionBoards[0].Firmware.Version);
            Assert.AreNotSame(original.Electronics.ExpansionBoards[0].VIn.Current, assigned.Electronics.ExpansionBoards[0].VIn.Current);
            Assert.AreNotSame(original.Electronics.ExpansionBoards[0].VIn.Min, assigned.Electronics.ExpansionBoards[0].VIn.Min);
            Assert.AreNotSame(original.Electronics.ExpansionBoards[0].VIn.Max, assigned.Electronics.ExpansionBoards[0].VIn.Max);
            Assert.AreNotSame(original.Electronics.ExpansionBoards[0].McuTemp.Current, assigned.Electronics.ExpansionBoards[0].McuTemp.Current);
            Assert.AreNotSame(original.Electronics.ExpansionBoards[0].McuTemp.Min, assigned.Electronics.ExpansionBoards[0].McuTemp.Min);
            Assert.AreNotSame(original.Electronics.ExpansionBoards[0].McuTemp.Max, assigned.Electronics.ExpansionBoards[0].McuTemp.Max);
            Assert.AreNotSame(original.Electronics.ExpansionBoards[0].MaxHeaters, assigned.Electronics.ExpansionBoards[0].MaxHeaters);
            Assert.AreNotSame(original.Electronics.ExpansionBoards[0].MaxMotors, assigned.Electronics.ExpansionBoards[0].MaxMotors);
        }
    }
}
