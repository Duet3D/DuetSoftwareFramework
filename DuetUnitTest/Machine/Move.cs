using DuetAPI.Machine.Move;
using NUnit.Framework;
using Model = DuetAPI.Machine.Model;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class Move
    {
        [Test]
        public void Clone()
        {
            Model original = new Model();

            Axis axis = new Axis
            {
                Drives = new uint[] { 1, 2 },
                Homed = true,
                Letter = 'X',
                MachinePosition = 123,
                Max = 456,
                Min = -789
            };
            original.Move.Axes.Add(axis);
            original.Move.BabystepZ = 0.34;
            original.Move.Compensation = "Compensation";
            original.Move.CurrentMove.RequestedSpeed = 45;
            original.Move.CurrentMove.TopSpeed = 30;

            Drive drive = new Drive
            {
                Acceleration = 12,
                Current = 1200,
                MaxSpeed = 400,
                MinSpeed = 10,
                Position = 56
            };
            drive.Microstepping.Interpolated = false;
            drive.Microstepping.Value = 256;
            original.Move.Drives.Add(drive);

            Extruder extruder = new Extruder
            {
                Factor = 1.23
            };
            extruder.Nonlinear.A = 1;
            extruder.Nonlinear.B = 2;
            extruder.Nonlinear.Temperature = 56;
            extruder.Nonlinear.UpperLimit = 78;
            original.Move.Extruders.Add(extruder);

            original.Move.Geometry.Type = "delta";
            original.Move.Idle.Factor = 0.8;
            original.Move.Idle.Timeout = 50;
            original.Move.SpeedFactor = 1.45;

            Model clone = (Model)original.Clone();

            Assert.AreEqual(1, original.Move.Axes.Count);
            Assert.AreEqual(original.Move.Axes[0].Drives, clone.Move.Axes[0].Drives);
            Assert.AreEqual(original.Move.Axes[0].Homed, clone.Move.Axes[0].Homed);
            Assert.AreEqual(original.Move.Axes[0].Letter, clone.Move.Axes[0].Letter);
            Assert.AreEqual(original.Move.Axes[0].MachinePosition, clone.Move.Axes[0].MachinePosition);
            Assert.AreEqual(original.Move.Axes[0].Max, clone.Move.Axes[0].Max);
            Assert.AreEqual(original.Move.Axes[0].Min, clone.Move.Axes[0].Min);

            Assert.AreEqual(original.Move.BabystepZ, clone.Move.BabystepZ);
            Assert.AreEqual(original.Move.Compensation, clone.Move.Compensation);
            Assert.AreEqual(original.Move.CurrentMove.RequestedSpeed, clone.Move.CurrentMove.RequestedSpeed);
            Assert.AreEqual(original.Move.CurrentMove.TopSpeed, clone.Move.CurrentMove.TopSpeed);

            Assert.AreEqual(1, original.Move.Drives.Count);
            Assert.AreEqual(original.Move.Drives[0].Acceleration, clone.Move.Drives[0].Acceleration);
            Assert.AreEqual(original.Move.Drives[0].Current, clone.Move.Drives[0].Current);
            Assert.AreEqual(original.Move.Drives[0].MaxSpeed, clone.Move.Drives[0].MaxSpeed);
            Assert.AreEqual(original.Move.Drives[0].Microstepping.Interpolated, clone.Move.Drives[0].Microstepping.Interpolated);
            Assert.AreEqual(original.Move.Drives[0].Microstepping.Value, clone.Move.Drives[0].Microstepping.Value);
            Assert.AreEqual(original.Move.Drives[0].MinSpeed, clone.Move.Drives[0].MinSpeed);
            Assert.AreEqual(original.Move.Drives[0].Position, clone.Move.Drives[0].Position);

            Assert.AreEqual(1, original.Move.Extruders.Count);
            Assert.AreEqual(original.Move.Extruders[0].Factor, clone.Move.Extruders[0].Factor);
            Assert.AreEqual(original.Move.Extruders[0].Nonlinear.A, clone.Move.Extruders[0].Nonlinear.A);
            Assert.AreEqual(original.Move.Extruders[0].Nonlinear.B, clone.Move.Extruders[0].Nonlinear.B);
            Assert.AreEqual(original.Move.Extruders[0].Nonlinear.Temperature, clone.Move.Extruders[0].Nonlinear.Temperature);
            Assert.AreEqual(original.Move.Extruders[0].Nonlinear.UpperLimit, clone.Move.Extruders[0].Nonlinear.UpperLimit);

            Assert.AreEqual(original.Move.Geometry.Type, clone.Move.Geometry.Type);
            Assert.AreEqual(original.Move.Idle.Factor, clone.Move.Idle.Factor);
            Assert.AreEqual(original.Move.Idle.Timeout, clone.Move.Idle.Timeout);
            Assert.AreEqual(original.Move.SpeedFactor, clone.Move.SpeedFactor);

            Assert.AreNotSame(original.Move.Axes[0].Drives, clone.Move.Axes[0].Drives);
            Assert.AreNotSame(original.Move.Axes[0].Homed, clone.Move.Axes[0].Homed);
            Assert.AreNotSame(original.Move.Axes[0].Letter, clone.Move.Axes[0].Letter);
            Assert.AreNotSame(original.Move.Axes[0].MachinePosition, clone.Move.Axes[0].MachinePosition);
            Assert.AreNotSame(original.Move.Axes[0].Max, clone.Move.Axes[0].Max);
            Assert.AreNotSame(original.Move.Axes[0].Min, clone.Move.Axes[0].Min);

            Assert.AreNotSame(original.Move.BabystepZ, clone.Move.BabystepZ);
            Assert.AreNotSame(original.Move.Compensation, clone.Move.Compensation);
            Assert.AreNotSame(original.Move.CurrentMove.RequestedSpeed, clone.Move.CurrentMove.RequestedSpeed);
            Assert.AreNotSame(original.Move.CurrentMove.TopSpeed, clone.Move.CurrentMove.TopSpeed);

            Assert.AreNotSame(original.Move.Drives[0].Acceleration, clone.Move.Drives[0].Acceleration);
            Assert.AreNotSame(original.Move.Drives[0].Current, clone.Move.Drives[0].Current);
            Assert.AreNotSame(original.Move.Drives[0].MaxSpeed, clone.Move.Drives[0].MaxSpeed);
            Assert.AreNotSame(original.Move.Drives[0].Microstepping.Interpolated, clone.Move.Drives[0].Microstepping.Interpolated);
            Assert.AreNotSame(original.Move.Drives[0].Microstepping.Value, clone.Move.Drives[0].Microstepping.Value);
            Assert.AreNotSame(original.Move.Drives[0].MinSpeed, clone.Move.Drives[0].MinSpeed);
            Assert.AreNotSame(original.Move.Drives[0].Position, clone.Move.Drives[0].Position);

            Assert.AreNotSame(original.Move.Extruders[0].Factor, clone.Move.Extruders[0].Factor);
            Assert.AreNotSame(original.Move.Extruders[0].Nonlinear.A, clone.Move.Extruders[0].Nonlinear.A);
            Assert.AreNotSame(original.Move.Extruders[0].Nonlinear.B, clone.Move.Extruders[0].Nonlinear.B);
            Assert.AreNotSame(original.Move.Extruders[0].Nonlinear.Temperature, clone.Move.Extruders[0].Nonlinear.Temperature);
            Assert.AreNotSame(original.Move.Extruders[0].Nonlinear.UpperLimit, clone.Move.Extruders[0].Nonlinear.UpperLimit);

            Assert.AreNotSame(original.Move.Geometry.Type, clone.Move.Geometry.Type);
            Assert.AreNotSame(original.Move.Idle.Factor, clone.Move.Idle.Factor);
            Assert.AreNotSame(original.Move.Idle.Timeout, clone.Move.Idle.Timeout);
            Assert.AreNotSame(original.Move.SpeedFactor, clone.Move.SpeedFactor);
        }
    }
}
