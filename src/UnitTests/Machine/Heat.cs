using DuetAPI.Machine;
using NUnit.Framework;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class Heat
    {
        [Test]
        public void Clone()
        {
            MachineModel original = new MachineModel();

            BedOrChamber bed = new BedOrChamber
            {
                Name = "Bed Name"
            };
            bed.Active.Add(123F);
            bed.Standby.Add(456F);
            bed.Heaters.Add(0);
            original.Heat.Beds.Add(null);
            original.Heat.Beds.Add(bed);

            BedOrChamber chamber = new BedOrChamber
            {
                Name = "Chamber Name"
            };
            chamber.Active.Add(321F);
            chamber.Standby.Add(654F);
            chamber.Heaters.Add(4);
            chamber.Heaters.Add(6);
            original.Heat.Chambers.Add(null);
            original.Heat.Chambers.Add(chamber);

            original.Heat.ColdExtrudeTemperature = 678F;
            original.Heat.ColdRetractTemperature = 987F;

            ExtraHeater extraHeater = new ExtraHeater
            {
                Current = 123,
                Name = "Extra Heater",
                Sensor = 4,
                State = HeaterState.Tuning
            };
            original.Heat.Extra.Add(extraHeater);

            Heater heater = new Heater
            {
                Current = 567,
                Max = 578,
                Sensor = 6,
                State = HeaterState.Standby
            };
            heater.Model.DeadTime = 322;
            heater.Model.Gain = 673;
            heater.Model.MaxPwm = 0.45F;
            heater.Model.TimeConstant = 32;
            original.Heat.Heaters.Add(heater);

            MachineModel clone = (MachineModel)original.Clone();

            Assert.AreEqual(2, original.Heat.Beds.Count);
            Assert.AreEqual(original.Heat.Beds[0], null);
            Assert.AreEqual(original.Heat.Beds[1].Active, clone.Heat.Beds[1].Active);
            Assert.AreEqual(original.Heat.Beds[1].Standby, clone.Heat.Beds[1].Standby);
            Assert.AreEqual(original.Heat.Beds[1].Name, clone.Heat.Beds[1].Name);
            Assert.AreEqual(original.Heat.Beds[1].Heaters, clone.Heat.Beds[1].Heaters);

            Assert.AreEqual(2, original.Heat.Chambers.Count);
            Assert.AreEqual(original.Heat.Chambers[0], null);
            Assert.AreEqual(original.Heat.Chambers[1].Active, clone.Heat.Chambers[1].Active);
            Assert.AreEqual(original.Heat.Chambers[1].Standby, clone.Heat.Chambers[1].Standby);
            Assert.AreEqual(original.Heat.Chambers[1].Name, clone.Heat.Chambers[1].Name);
            Assert.AreEqual(original.Heat.Chambers[1].Heaters, clone.Heat.Chambers[1].Heaters);

            Assert.AreEqual(original.Heat.ColdExtrudeTemperature, clone.Heat.ColdExtrudeTemperature);
            Assert.AreEqual(original.Heat.ColdRetractTemperature, clone.Heat.ColdRetractTemperature);

            Assert.AreEqual(1, original.Heat.Extra.Count);
            Assert.AreEqual(original.Heat.Extra[0].Current, clone.Heat.Extra[0].Current);
            Assert.AreEqual(original.Heat.Extra[0].Name, clone.Heat.Extra[0].Name);
            Assert.AreEqual(original.Heat.Extra[0].Sensor, clone.Heat.Extra[0].Sensor);
            Assert.AreEqual(original.Heat.Extra[0].State, clone.Heat.Extra[0].State);

            Assert.AreEqual(1, original.Heat.Heaters.Count);
            Assert.AreEqual(original.Heat.Heaters[0].Current, clone.Heat.Heaters[0].Current);
            Assert.AreEqual(original.Heat.Heaters[0].Max, clone.Heat.Heaters[0].Max);
            Assert.AreEqual(original.Heat.Heaters[0].Sensor, clone.Heat.Heaters[0].Sensor);
            Assert.AreEqual(original.Heat.Heaters[0].State, clone.Heat.Heaters[0].State);
            Assert.AreEqual(original.Heat.Heaters[0].Model.DeadTime, clone.Heat.Heaters[0].Model.DeadTime);
            Assert.AreEqual(original.Heat.Heaters[0].Model.Gain, clone.Heat.Heaters[0].Model.Gain);
            Assert.AreEqual(original.Heat.Heaters[0].Model.MaxPwm, clone.Heat.Heaters[0].Model.MaxPwm);
            Assert.AreEqual(original.Heat.Heaters[0].Model.TimeConstant, clone.Heat.Heaters[0].Model.TimeConstant);
        }

        [Test]
        public void Assigned()
        {
            MachineModel original = new MachineModel();

            BedOrChamber bed = new BedOrChamber
            {
                Name = "Bed Name"
            };
            bed.Active.Add(123F);
            bed.Standby.Add(456F);
            bed.Heaters.Add(0);
            original.Heat.Beds.Add(null);
            original.Heat.Beds.Add(bed);

            BedOrChamber chamber = new BedOrChamber
            {
                Name = "Chamber Name"
            };
            chamber.Active.Add(321F);
            chamber.Standby.Add(654F);
            chamber.Heaters.Add(4);
            chamber.Heaters.Add(6);
            original.Heat.Chambers.Add(null);
            original.Heat.Chambers.Add(chamber);

            original.Heat.ColdExtrudeTemperature = 678F;
            original.Heat.ColdRetractTemperature = 987F;

            ExtraHeater extraHeater = new ExtraHeater
            {
                Current = 123,
                Name = "Extra Heater",
                Sensor = 4,
                State = HeaterState.Tuning
            };
            original.Heat.Extra.Add(extraHeater);

            Heater heater = new Heater
            {
                Current = 567,
                Max = 578,
                Sensor = 6,
                State = HeaterState.Standby
            };
            heater.Model.DeadTime = 322;
            heater.Model.Gain = 673;
            heater.Model.MaxPwm = 0.45F;
            heater.Model.TimeConstant = 32;
            original.Heat.Heaters.Add(heater);

            MachineModel assigned = new MachineModel();
            assigned.Assign(original);

            Assert.AreEqual(2, original.Heat.Beds.Count);
            Assert.AreEqual(original.Heat.Beds[0], null);
            Assert.AreEqual(original.Heat.Beds[1].Active, assigned.Heat.Beds[1].Active);
            Assert.AreEqual(original.Heat.Beds[1].Standby, assigned.Heat.Beds[1].Standby);
            Assert.AreEqual(original.Heat.Beds[1].Name, assigned.Heat.Beds[1].Name);
            Assert.AreEqual(original.Heat.Beds[1].Heaters, assigned.Heat.Beds[1].Heaters);

            Assert.AreEqual(2, original.Heat.Chambers.Count);
            Assert.AreEqual(original.Heat.Chambers[0], null);
            Assert.AreEqual(original.Heat.Chambers[1].Active, assigned.Heat.Chambers[1].Active);
            Assert.AreEqual(original.Heat.Chambers[1].Standby, assigned.Heat.Chambers[1].Standby);
            Assert.AreEqual(original.Heat.Chambers[1].Name, assigned.Heat.Chambers[1].Name);
            Assert.AreEqual(original.Heat.Chambers[1].Heaters, assigned.Heat.Chambers[1].Heaters);

            Assert.AreEqual(original.Heat.ColdExtrudeTemperature, assigned.Heat.ColdExtrudeTemperature);
            Assert.AreEqual(original.Heat.ColdRetractTemperature, assigned.Heat.ColdRetractTemperature);

            Assert.AreEqual(1, original.Heat.Extra.Count);
            Assert.AreEqual(original.Heat.Extra[0].Current, assigned.Heat.Extra[0].Current);
            Assert.AreEqual(original.Heat.Extra[0].Name, assigned.Heat.Extra[0].Name);
            Assert.AreEqual(original.Heat.Extra[0].Sensor, assigned.Heat.Extra[0].Sensor);
            Assert.AreEqual(original.Heat.Extra[0].State, assigned.Heat.Extra[0].State);

            Assert.AreEqual(1, original.Heat.Heaters.Count);
            Assert.AreEqual(original.Heat.Heaters[0].Current, assigned.Heat.Heaters[0].Current);
            Assert.AreEqual(original.Heat.Heaters[0].Max, assigned.Heat.Heaters[0].Max);
            Assert.AreEqual(original.Heat.Heaters[0].Sensor, assigned.Heat.Heaters[0].Sensor);
            Assert.AreEqual(original.Heat.Heaters[0].State, assigned.Heat.Heaters[0].State);
            Assert.AreEqual(original.Heat.Heaters[0].Model.DeadTime, assigned.Heat.Heaters[0].Model.DeadTime);
            Assert.AreEqual(original.Heat.Heaters[0].Model.Gain, assigned.Heat.Heaters[0].Model.Gain);
            Assert.AreEqual(original.Heat.Heaters[0].Model.MaxPwm, assigned.Heat.Heaters[0].Model.MaxPwm);
            Assert.AreEqual(original.Heat.Heaters[0].Model.TimeConstant, assigned.Heat.Heaters[0].Model.TimeConstant);
        }
    }
}
