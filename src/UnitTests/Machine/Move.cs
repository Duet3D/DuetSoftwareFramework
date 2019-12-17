using DuetAPI.Machine;
using NUnit.Framework;

namespace UnitTests.Machine
{
    [TestFixture]
    public class Move
    {
        [Test]
        public void Clone()
        {
            MachineModel original = new MachineModel();

            Axis axis = new Axis
            {
                Homed = true,
                Letter = 'X',
                MachinePosition = 123,
                Min = -789,
                MinEndstop = 1,
                MinProbed = true,
                Max = 456,
                MaxEndstop = 2,
                MaxProbed = true
            };
            axis.Drives.Add(1);
            axis.Drives.Add(2);
            original.Move.Axes.Add(axis);
            original.Move.BabystepZ = 0.34F;
            original.Move.Compensation = "Compensation";
            original.Move.CurrentMove.RequestedSpeed = 45;
            original.Move.CurrentMove.TopSpeed = 30;

            Drive drive = new Drive
            {
                Acceleration = 12,
                Current = 1200,
                MinSpeed = 10,
                MaxSpeed = 400,
                Position = 56
            };
            drive.Microstepping.Interpolated = false;
            drive.Microstepping.Value = 256;
            original.Move.Drives.Add(drive);

            Extruder extruder = new Extruder
            {
                Factor = 1.23F
            };
            extruder.Nonlinear.A = 1;
            extruder.Nonlinear.B = 2;
            extruder.Nonlinear.Temperature = 56;
            extruder.Nonlinear.UpperLimit = 78;
            original.Move.Extruders.Add(extruder);

            original.Move.Geometry.Type = GeometryType.Delta;
            original.Move.Idle.Factor = 0.8F;
            original.Move.Idle.Timeout = 50;
            original.Move.SpeedFactor = 1.45F;

            MachineModel clone = (MachineModel)original.Clone();

            Assert.AreEqual(1, clone.Move.Axes.Count);
            Assert.AreEqual(original.Move.Axes[0].Drives, clone.Move.Axes[0].Drives);
            Assert.AreEqual(original.Move.Axes[0].Homed, clone.Move.Axes[0].Homed);
            Assert.AreEqual(original.Move.Axes[0].Letter, clone.Move.Axes[0].Letter);
            Assert.AreEqual(original.Move.Axes[0].MachinePosition, clone.Move.Axes[0].MachinePosition);
            Assert.AreEqual(original.Move.Axes[0].Min, clone.Move.Axes[0].Min);
            Assert.AreEqual(original.Move.Axes[0].MinEndstop, clone.Move.Axes[0].MinEndstop);
            Assert.AreEqual(original.Move.Axes[0].MinProbed, clone.Move.Axes[0].MinProbed);
            Assert.AreEqual(original.Move.Axes[0].Max, clone.Move.Axes[0].Max);
            Assert.AreEqual(original.Move.Axes[0].MaxEndstop, clone.Move.Axes[0].MaxEndstop);
            Assert.AreEqual(original.Move.Axes[0].MaxProbed, clone.Move.Axes[0].MaxProbed);

            Assert.AreEqual(original.Move.BabystepZ, clone.Move.BabystepZ);
            Assert.AreEqual(original.Move.Compensation, clone.Move.Compensation);
            Assert.AreEqual(original.Move.CurrentMove.RequestedSpeed, clone.Move.CurrentMove.RequestedSpeed);
            Assert.AreEqual(original.Move.CurrentMove.TopSpeed, clone.Move.CurrentMove.TopSpeed);

            Assert.AreEqual(1, clone.Move.Drives.Count);
            Assert.AreEqual(original.Move.Drives[0].Acceleration, clone.Move.Drives[0].Acceleration);
            Assert.AreEqual(original.Move.Drives[0].Current, clone.Move.Drives[0].Current);
            Assert.AreEqual(original.Move.Drives[0].MaxSpeed, clone.Move.Drives[0].MaxSpeed);
            Assert.AreEqual(original.Move.Drives[0].Microstepping.Interpolated, clone.Move.Drives[0].Microstepping.Interpolated);
            Assert.AreEqual(original.Move.Drives[0].Microstepping.Value, clone.Move.Drives[0].Microstepping.Value);
            Assert.AreEqual(original.Move.Drives[0].MinSpeed, clone.Move.Drives[0].MinSpeed);
            Assert.AreEqual(original.Move.Drives[0].Position, clone.Move.Drives[0].Position);

            Assert.AreEqual(1, clone.Move.Extruders.Count);
            Assert.AreEqual(original.Move.Extruders[0].Factor, clone.Move.Extruders[0].Factor);
            Assert.AreEqual(original.Move.Extruders[0].Nonlinear.A, clone.Move.Extruders[0].Nonlinear.A);
            Assert.AreEqual(original.Move.Extruders[0].Nonlinear.B, clone.Move.Extruders[0].Nonlinear.B);
            Assert.AreEqual(original.Move.Extruders[0].Nonlinear.Temperature, clone.Move.Extruders[0].Nonlinear.Temperature);
            Assert.AreEqual(original.Move.Extruders[0].Nonlinear.UpperLimit, clone.Move.Extruders[0].Nonlinear.UpperLimit);

            Assert.AreEqual(original.Move.Geometry.Type, clone.Move.Geometry.Type);
            Assert.AreEqual(original.Move.Idle.Factor, clone.Move.Idle.Factor);
            Assert.AreEqual(original.Move.Idle.Timeout, clone.Move.Idle.Timeout);
            Assert.AreEqual(original.Move.SpeedFactor, clone.Move.SpeedFactor);
        }

        [Test]
        public void Assign()
        {
            MachineModel original = new MachineModel();

            Axis axis = new Axis
            {
                Homed = true,
                Letter = 'X',
                MachinePosition = 123,
                Min = -789,
                MinEndstop = 1,
                MinProbed = true,
                Max = 456,
                MaxEndstop = 2,
                MaxProbed = true
            };
            axis.Drives.Add(1);
            axis.Drives.Add(2);
            original.Move.Axes.Add(axis);
            original.Move.BabystepZ = 0.34F;
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
                Factor = 1.23F
            };
            extruder.Nonlinear.A = 1;
            extruder.Nonlinear.B = 2;
            extruder.Nonlinear.Temperature = 56;
            extruder.Nonlinear.UpperLimit = 78;
            original.Move.Extruders.Add(extruder);

            original.Move.Geometry.Type = GeometryType.Delta;
            original.Move.Idle.Factor = 0.8F;
            original.Move.Idle.Timeout = 50;
            original.Move.SpeedFactor = 1.45F;

            MachineModel assigned = new MachineModel();
            assigned.Assign(original);

            Assert.AreEqual(1, assigned.Move.Axes.Count);
            Assert.AreEqual(original.Move.Axes[0].Drives, assigned.Move.Axes[0].Drives);
            Assert.AreEqual(original.Move.Axes[0].Homed, assigned.Move.Axes[0].Homed);
            Assert.AreEqual(original.Move.Axes[0].Letter, assigned.Move.Axes[0].Letter);
            Assert.AreEqual(original.Move.Axes[0].MachinePosition, assigned.Move.Axes[0].MachinePosition);
            Assert.AreEqual(original.Move.Axes[0].Min, assigned.Move.Axes[0].Min);
            Assert.AreEqual(original.Move.Axes[0].MinEndstop, assigned.Move.Axes[0].MinEndstop);
            Assert.AreEqual(original.Move.Axes[0].MinProbed, assigned.Move.Axes[0].MinProbed);
            Assert.AreEqual(original.Move.Axes[0].Max, assigned.Move.Axes[0].Max);
            Assert.AreEqual(original.Move.Axes[0].MaxEndstop, assigned.Move.Axes[0].MaxEndstop);
            Assert.AreEqual(original.Move.Axes[0].MaxProbed, assigned.Move.Axes[0].MaxProbed);

            Assert.AreEqual(original.Move.BabystepZ, assigned.Move.BabystepZ);
            Assert.AreEqual(original.Move.Compensation, assigned.Move.Compensation);
            Assert.AreEqual(original.Move.CurrentMove.RequestedSpeed, assigned.Move.CurrentMove.RequestedSpeed);
            Assert.AreEqual(original.Move.CurrentMove.TopSpeed, assigned.Move.CurrentMove.TopSpeed);

            Assert.AreEqual(1, assigned.Move.Drives.Count);
            Assert.AreEqual(original.Move.Drives[0].Acceleration, assigned.Move.Drives[0].Acceleration);
            Assert.AreEqual(original.Move.Drives[0].Current, assigned.Move.Drives[0].Current);
            Assert.AreEqual(original.Move.Drives[0].MaxSpeed, assigned.Move.Drives[0].MaxSpeed);
            Assert.AreEqual(original.Move.Drives[0].Microstepping.Interpolated, assigned.Move.Drives[0].Microstepping.Interpolated);
            Assert.AreEqual(original.Move.Drives[0].Microstepping.Value, assigned.Move.Drives[0].Microstepping.Value);
            Assert.AreEqual(original.Move.Drives[0].MinSpeed, assigned.Move.Drives[0].MinSpeed);
            Assert.AreEqual(original.Move.Drives[0].Position, assigned.Move.Drives[0].Position);

            Assert.AreEqual(1, assigned.Move.Extruders.Count);
            Assert.AreEqual(original.Move.Extruders[0].Factor, assigned.Move.Extruders[0].Factor);
            Assert.AreEqual(original.Move.Extruders[0].Nonlinear.A, assigned.Move.Extruders[0].Nonlinear.A);
            Assert.AreEqual(original.Move.Extruders[0].Nonlinear.B, assigned.Move.Extruders[0].Nonlinear.B);
            Assert.AreEqual(original.Move.Extruders[0].Nonlinear.Temperature, assigned.Move.Extruders[0].Nonlinear.Temperature);
            Assert.AreEqual(original.Move.Extruders[0].Nonlinear.UpperLimit, assigned.Move.Extruders[0].Nonlinear.UpperLimit);

            Assert.AreEqual(original.Move.Geometry.Type, assigned.Move.Geometry.Type);
            Assert.AreEqual(original.Move.Idle.Factor, assigned.Move.Idle.Factor);
            Assert.AreEqual(original.Move.Idle.Timeout, assigned.Move.Idle.Timeout);
            Assert.AreEqual(original.Move.SpeedFactor, assigned.Move.SpeedFactor);
        }
    }
}
