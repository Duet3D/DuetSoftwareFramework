using NUnit.Framework;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class Electronics
    {
        [Test]
        public void Clone()
        {
            DuetAPI.Machine.Model original = new DuetAPI.Machine.Model();
            original.Electronics.Type = "Electronics Type";
            original.Electronics.Name = "Electronics Name";
            original.Electronics.Revision = "Electronics Revision";
            original.Electronics.Firmware.Name = "Firmware Name";
            original.Electronics.Firmware.Version = "Firmware Version";
            original.Electronics.Firmware.Date = "Firmware Date";
            original.Electronics.ProcessorID = "Processor ID";
            original.Electronics.VIn.Current = 321;
            original.Electronics.VIn.Min = 654;
            original.Electronics.VIn.Max = 987;
            original.Electronics.McuTemp.Current = 123;
            original.Electronics.McuTemp.Min = 456;
            original.Electronics.McuTemp.Max = 789;

            DuetAPI.Machine.Electronics.ExpansionBoard expansionBoard = new DuetAPI.Machine.Electronics.ExpansionBoard();
            expansionBoard.Name = "Expansion Name";
            expansionBoard.Revision = "Expansion Revision";
            expansionBoard.Firmware.Name = "Expansion Firmware Name";
            expansionBoard.Firmware.Date = "Expansion Firmware Date";
            expansionBoard.Firmware.Version = "Expansion Firmware Version";
            expansionBoard.VIn.Current = 321;
            expansionBoard.VIn.Min = 654;
            expansionBoard.VIn.Max = 987;
            expansionBoard.McuTemp.Current = 123;
            expansionBoard.McuTemp.Min = 456;
            expansionBoard.McuTemp.Max = 789;
            expansionBoard.MaxHeaters = 12;
            expansionBoard.MaxMotors = 6;
            original.Electronics.ExpansionBoards.Add(expansionBoard);

            DuetAPI.Machine.Model clone = (DuetAPI.Machine.Model)original.Clone();

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
    }
}
