using DuetAPI.Machine;
using NUnit.Framework;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class Sensors
    {
        [Test]
        public void Clone()
        {
            MachineModel original = new MachineModel();

            Endstop endstop = new Endstop
            {
                Position = EndstopPosition.HighEnd,
                Triggered = true,
                Type = EndstopType.MotorLoadDetection
            };
            original.Sensors.Endstops.Add(endstop);

            Probe probe = new Probe
            {
                DisablesBed = true,
                DiveHeight = 45.6F,
                Inverted = true,
                MaxProbeCount = 4,
                RecoveryTime = 0.65F,
                Speed = 456,
                Threshold = 678,
                Tolerance = 0.42F,
                TravelSpeed = 500,
                TriggerHeight = 1.23F,
                Type = ProbeType.Switch,
                Value = 45
            };
            probe.SecondaryValues.Add(12);
            probe.SecondaryValues.Add(34);
            original.Sensors.Probes.Add(probe);

            MachineModel clone = (MachineModel)original.Clone();

            Assert.AreEqual(1, original.Sensors.Endstops.Count);
            Assert.AreEqual(original.Sensors.Endstops[0].Position, clone.Sensors.Endstops[0].Position);
            Assert.AreEqual(original.Sensors.Endstops[0].Triggered, clone.Sensors.Endstops[0].Triggered);
            Assert.AreEqual(original.Sensors.Endstops[0].Type, clone.Sensors.Endstops[0].Type);

            Assert.AreEqual(1, original.Sensors.Probes.Count);
            Assert.AreEqual(original.Sensors.Probes[0].DisablesBed, clone.Sensors.Probes[0].DisablesBed);
            Assert.AreEqual(original.Sensors.Probes[0].DiveHeight, clone.Sensors.Probes[0].DiveHeight);
            Assert.AreEqual(original.Sensors.Probes[0].Inverted, clone.Sensors.Probes[0].Inverted);
            Assert.AreEqual(original.Sensors.Probes[0].MaxProbeCount, clone.Sensors.Probes[0].MaxProbeCount);
            Assert.AreEqual(original.Sensors.Probes[0].RecoveryTime, clone.Sensors.Probes[0].RecoveryTime);
            Assert.AreEqual(original.Sensors.Probes[0].SecondaryValues, clone.Sensors.Probes[0].SecondaryValues);
            Assert.AreEqual(original.Sensors.Probes[0].Speed, clone.Sensors.Probes[0].Speed);
            Assert.AreEqual(original.Sensors.Probes[0].Threshold, clone.Sensors.Probes[0].Threshold);
            Assert.AreEqual(original.Sensors.Probes[0].Tolerance, clone.Sensors.Probes[0].Tolerance);
            Assert.AreEqual(original.Sensors.Probes[0].TravelSpeed, clone.Sensors.Probes[0].TravelSpeed);
            Assert.AreEqual(original.Sensors.Probes[0].TriggerHeight, clone.Sensors.Probes[0].TriggerHeight);
            Assert.AreEqual(original.Sensors.Probes[0].Type, clone.Sensors.Probes[0].Type);
            Assert.AreEqual(original.Sensors.Probes[0].Value, clone.Sensors.Probes[0].Value);

            Assert.AreNotSame(original.Sensors.Endstops[0].Position, clone.Sensors.Endstops[0].Position);
            Assert.AreNotSame(original.Sensors.Endstops[0].Triggered, clone.Sensors.Endstops[0].Triggered);
            Assert.AreNotSame(original.Sensors.Endstops[0].Type, clone.Sensors.Endstops[0].Type);

            Assert.AreNotSame(original.Sensors.Probes[0].DisablesBed, clone.Sensors.Probes[0].DisablesBed);
            Assert.AreNotSame(original.Sensors.Probes[0].DiveHeight, clone.Sensors.Probes[0].DiveHeight);
            Assert.AreNotSame(original.Sensors.Probes[0].Inverted, clone.Sensors.Probes[0].Inverted);
            Assert.AreNotSame(original.Sensors.Probes[0].MaxProbeCount, clone.Sensors.Probes[0].MaxProbeCount);
            Assert.AreNotSame(original.Sensors.Probes[0].RecoveryTime, clone.Sensors.Probes[0].RecoveryTime);
            Assert.AreNotSame(original.Sensors.Probes[0].SecondaryValues, clone.Sensors.Probes[0].SecondaryValues);
            Assert.AreNotSame(original.Sensors.Probes[0].Speed, clone.Sensors.Probes[0].Speed);
            Assert.AreNotSame(original.Sensors.Probes[0].Threshold, clone.Sensors.Probes[0].Threshold);
            Assert.AreNotSame(original.Sensors.Probes[0].Tolerance, clone.Sensors.Probes[0].Tolerance);
            Assert.AreNotSame(original.Sensors.Probes[0].TravelSpeed, clone.Sensors.Probes[0].TravelSpeed);
            Assert.AreNotSame(original.Sensors.Probes[0].TriggerHeight, clone.Sensors.Probes[0].TriggerHeight);
            Assert.AreNotSame(original.Sensors.Probes[0].Type, clone.Sensors.Probes[0].Type);
            Assert.AreNotSame(original.Sensors.Probes[0].Value, clone.Sensors.Probes[0].Value);
        }

        [Test]
        public void Assign()
        {
            MachineModel original = new MachineModel();

            Endstop endstop = new Endstop
            {
                Position = EndstopPosition.HighEnd,
                Triggered = true,
                Type = EndstopType.MotorLoadDetection
            };
            original.Sensors.Endstops.Add(endstop);

            Probe probe = new Probe
            {
                DisablesBed = true,
                DiveHeight = 45.6F,
                Inverted = true,
                MaxProbeCount = 4,
                RecoveryTime = 0.65F,
                Speed = 456,
                Threshold = 678,
                Tolerance = 0.42F,
                TravelSpeed = 500,
                TriggerHeight = 1.23F,
                Type = ProbeType.Switch,
                Value = 45
            };
            probe.SecondaryValues.Add(12);
            probe.SecondaryValues.Add(34);
            original.Sensors.Probes.Add(probe);

            MachineModel assigned = new MachineModel();
            assigned.Assign(original);

            Assert.AreEqual(1, original.Sensors.Endstops.Count);
            Assert.AreEqual(original.Sensors.Endstops[0].Position, assigned.Sensors.Endstops[0].Position);
            Assert.AreEqual(original.Sensors.Endstops[0].Triggered, assigned.Sensors.Endstops[0].Triggered);
            Assert.AreEqual(original.Sensors.Endstops[0].Type, assigned.Sensors.Endstops[0].Type);

            Assert.AreEqual(1, original.Sensors.Probes.Count);
            Assert.AreEqual(original.Sensors.Probes[0].DisablesBed, assigned.Sensors.Probes[0].DisablesBed);
            Assert.AreEqual(original.Sensors.Probes[0].DiveHeight, assigned.Sensors.Probes[0].DiveHeight);
            Assert.AreEqual(original.Sensors.Probes[0].Inverted, assigned.Sensors.Probes[0].Inverted);
            Assert.AreEqual(original.Sensors.Probes[0].MaxProbeCount, assigned.Sensors.Probes[0].MaxProbeCount);
            Assert.AreEqual(original.Sensors.Probes[0].RecoveryTime, assigned.Sensors.Probes[0].RecoveryTime);
            Assert.AreEqual(original.Sensors.Probes[0].SecondaryValues, assigned.Sensors.Probes[0].SecondaryValues);
            Assert.AreEqual(original.Sensors.Probes[0].Speed, assigned.Sensors.Probes[0].Speed);
            Assert.AreEqual(original.Sensors.Probes[0].Threshold, assigned.Sensors.Probes[0].Threshold);
            Assert.AreEqual(original.Sensors.Probes[0].Tolerance, assigned.Sensors.Probes[0].Tolerance);
            Assert.AreEqual(original.Sensors.Probes[0].TravelSpeed, assigned.Sensors.Probes[0].TravelSpeed);
            Assert.AreEqual(original.Sensors.Probes[0].TriggerHeight, assigned.Sensors.Probes[0].TriggerHeight);
            Assert.AreEqual(original.Sensors.Probes[0].Type, assigned.Sensors.Probes[0].Type);
            Assert.AreEqual(original.Sensors.Probes[0].Value, assigned.Sensors.Probes[0].Value);

            Assert.AreNotSame(original.Sensors.Endstops[0].Position, assigned.Sensors.Endstops[0].Position);
            Assert.AreNotSame(original.Sensors.Endstops[0].Triggered, assigned.Sensors.Endstops[0].Triggered);
            Assert.AreNotSame(original.Sensors.Endstops[0].Type, assigned.Sensors.Endstops[0].Type);

            Assert.AreNotSame(original.Sensors.Probes[0].DisablesBed, assigned.Sensors.Probes[0].DisablesBed);
            Assert.AreNotSame(original.Sensors.Probes[0].DiveHeight, assigned.Sensors.Probes[0].DiveHeight);
            Assert.AreNotSame(original.Sensors.Probes[0].Inverted, assigned.Sensors.Probes[0].Inverted);
            Assert.AreNotSame(original.Sensors.Probes[0].MaxProbeCount, assigned.Sensors.Probes[0].MaxProbeCount);
            Assert.AreNotSame(original.Sensors.Probes[0].RecoveryTime, assigned.Sensors.Probes[0].RecoveryTime);
            Assert.AreNotSame(original.Sensors.Probes[0].SecondaryValues, assigned.Sensors.Probes[0].SecondaryValues);
            Assert.AreNotSame(original.Sensors.Probes[0].Speed, assigned.Sensors.Probes[0].Speed);
            Assert.AreNotSame(original.Sensors.Probes[0].Threshold, assigned.Sensors.Probes[0].Threshold);
            Assert.AreNotSame(original.Sensors.Probes[0].Tolerance, assigned.Sensors.Probes[0].Tolerance);
            Assert.AreNotSame(original.Sensors.Probes[0].TravelSpeed, assigned.Sensors.Probes[0].TravelSpeed);
            Assert.AreNotSame(original.Sensors.Probes[0].TriggerHeight, assigned.Sensors.Probes[0].TriggerHeight);
            Assert.AreNotSame(original.Sensors.Probes[0].Type, assigned.Sensors.Probes[0].Type);
            Assert.AreNotSame(original.Sensors.Probes[0].Value, assigned.Sensors.Probes[0].Value);
        }
    }
}
