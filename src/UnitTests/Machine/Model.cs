using DuetAPI.ObjectModel;
using NUnit.Framework;
using System.IO;
using System.Text.Json;

namespace UnitTests.Machine
{
    [TestFixture]
    public class Model
    {
        static void TestLoadedModel(ObjectModel model)
        {
            // Test all supported data types
            Assert.That(model.Directories.System, Is.EqualTo("0:/sys/custom"));
            Assert.That(model.Move.Axes[0].Letter, Is.EqualTo('W'));
            Assert.That(model.State.AtxPower, Is.True);
            Assert.That(model.Heat.ColdExtrudeTemperature, Is.EqualTo(145F));
            Assert.That(model.Job.FilePosition, Is.EqualTo(12345678));

            // Test nullable ModelObject
            Assert.That(model.State.MessageBox, Is.Not.Null);
            Assert.That(model.State.MessageBox.Mode, Is.EqualTo(MessageBoxMode.OkOnly));
            Assert.That(model.State.MessageBox.Message, Is.EqualTo("message"));
            Assert.That(model.State.MessageBox.Title, Is.EqualTo("title"));

            // Test nullable ModelObject in collection
            Assert.That(model.Heat.Heaters.Count, Is.EqualTo(2));
            Assert.That(model.Heat.Heaters[0], Is.Null);
            Assert.That(model.Heat.Heaters[1].Current, Is.EqualTo(25.01F));

            // Test polymorphic ModelObject
            Assert.That(model.Move.Kinematics, Is.TypeOf<CoreKinematics>());
            Assert.That(((CoreKinematics)model.Move.Kinematics).Name, Is.EqualTo(KinematicsName.Cartesian));

            // Test polymorphic ModelObject in collection
            Assert.That(model.Sensors.FilamentMonitors[0], Is.TypeOf<RotatingMagnetFilamentMonitor>());
            Assert.That(model.Sensors.FilamentMonitors[0].Type, Is.EqualTo(FilamentMonitorType.RotatingMagnet));
        }

        [Test]
        public void UpdateFromJson()
        {
            string modelPath = Path.Combine(Directory.GetCurrentDirectory(), "../../../Machine/JSON/model.json");
            string jsonText = System.IO.File.ReadAllText(modelPath);
            using JsonDocument parsedJson = JsonDocument.Parse(jsonText);

            ObjectModel model = new();
            model.UpdateFromJson(parsedJson.RootElement, false);

            // Test the loaded model values
            TestLoadedModel(model);

            // Serialize OM again and make sure it matches the saved state
            string serializedModel = JsonSerializer.Serialize(model, DuetAPI.Utility.JsonHelper.DefaultJsonOptions);
            Assert.That(serializedModel, Is.EqualTo(jsonText));
        }


        [Test]
        public void UpdateFromJsonReader()
        {
            string modelPath = Path.Combine(Directory.GetCurrentDirectory(), "../../../Machine/JSON/model.json");
            Utf8JsonReader reader = new(System.IO.File.ReadAllBytes(modelPath));
            reader.Read();

            ObjectModel model = new();
            model.UpdateFromJsonReader(ref reader, false);

            // Test the loaded model values
            TestLoadedModel(model);

            // Serialize OM again and make sure it matches the saved state
            string serializedModel = JsonSerializer.Serialize(model, DuetAPI.Utility.JsonHelper.DefaultJsonOptions);
            Assert.That(serializedModel, Is.EqualTo(System.IO.File.ReadAllText(modelPath)));
        }

        [Test]
        public void UpdateFromFirmware()
        {
            string modelPath = Path.Combine(Directory.GetCurrentDirectory(), "../../../Machine/JSON/stateKey.json");
            string jsonText = System.IO.File.ReadAllText(modelPath);
            using JsonDocument parsedJson = JsonDocument.Parse(jsonText);

            ObjectModel model = new();
            bool success = model.UpdateFromFirmwareJson("state", parsedJson.RootElement);

            Assert.That(success, Is.True);
        }

        [Test]
        public void UpdateFromFirmwareReader()
        {
            string modelPath = Path.Combine(Directory.GetCurrentDirectory(), "../../../Machine/JSON/stateKey.json");
            Utf8JsonReader reader = new(System.IO.File.ReadAllBytes(modelPath));
            reader.Read();

            ObjectModel model = new();
            bool success = model.UpdateFromFirmwareJsonReader("state", ref reader);

            Assert.That(success, Is.True);
        }

        private static readonly int[] expectedBedHeaters = [0, 1];

        [Test]
        public void UpdateFromOther()
        {
            ObjectModel modelToUpdate = new();
            modelToUpdate.Boards.Add(new Board
            {
                FirmwareName = "Foobar"
            });
            modelToUpdate.Heat.BedHeaters.Add(-1);
            modelToUpdate.Heat.BedHeaters.Add(1);
            modelToUpdate.Heat.BedHeaters.Add(2);
            modelToUpdate.Heat.Heaters.Add(null);
            modelToUpdate.Heat.Heaters.Add(new Heater
            {
                Standby = 20F
            });
            modelToUpdate.Heat.Heaters.Add(new Heater
            {
                Active = 45F
            });
            modelToUpdate.State.Status = MachineStatus.Busy;

            ObjectModel updatedModel = new();
            updatedModel.Boards.Add(new Board
            {
                FirmwareName = "Yum"
            });
            updatedModel.Heat.BedHeaters.Add(0);
            updatedModel.Heat.BedHeaters.Add(1);
            updatedModel.Heat.Heaters.Add(new Heater
            {
                Active = 90F,
                Standby = 21F
            });
            updatedModel.Heat.Heaters.Add(new Heater
            {
                Standby = 20F
            });
            updatedModel.Fans.Add(new Fan
            {
                ActualValue = 0.5F,
                RequestedValue = 0.75F
            });
            updatedModel.State.Status = MachineStatus.Pausing;

            byte[] json = updatedModel.ToUtf8Json();
            using JsonDocument jsonPatch = JsonDocument.Parse(json);
            modelToUpdate.UpdateFromJson(jsonPatch.RootElement, false);

            Assert.That(modelToUpdate.Boards[0].FirmwareName, Is.EqualTo("Yum"));
            Assert.That(modelToUpdate.Heat.BedHeaters, Is.EquivalentTo(expectedBedHeaters));
            Assert.That(modelToUpdate.Heat.Heaters[0].Active, Is.EqualTo(90F));
            Assert.That(modelToUpdate.Heat.Heaters[0].Standby, Is.EqualTo(21F));
            Assert.That(modelToUpdate.Heat.Heaters[1].Standby, Is.EqualTo(20F));
            Assert.That(modelToUpdate.Fans[0].ActualValue, Is.EqualTo(0.5F));
            Assert.That(modelToUpdate.Fans[0].RequestedValue, Is.EqualTo(0.75F));
            Assert.That(modelToUpdate.State.Status, Is.EqualTo(MachineStatus.Pausing));
        }

        [Test]
        public void Patch()
        {
            string modelPath = Path.Combine(Directory.GetCurrentDirectory(), "../../../Machine/JSON/model.json");
            string jsonText = System.IO.File.ReadAllText(modelPath);
            using JsonDocument parsedJson = JsonDocument.Parse(jsonText);
            ObjectModel model = new();
            model.UpdateFromJson(parsedJson.RootElement, false);

            string patchPath = Path.Combine(Directory.GetCurrentDirectory(), "../../../Machine/JSON/patch.json");
            string patchText = System.IO.File.ReadAllText(patchPath);
            using JsonDocument patchJson = JsonDocument.Parse(patchText);
            model.UpdateFromJson(patchJson.RootElement, false);

            Assert.That(model.Boards[0].FirmwareName, Is.EqualTo("Test"));
        }

        [Test]
        public void Assign()
        {
            string modelPath = Path.Combine(Directory.GetCurrentDirectory(), "../../../Machine/JSON/model.json");
            string jsonText = System.IO.File.ReadAllText(modelPath);
            using JsonDocument parsedJson = JsonDocument.Parse(jsonText);

            ObjectModel model = new();
            model.UpdateFromJson(parsedJson.RootElement, false);

            ObjectModel newModel = new();
            newModel.Assign(model);
            TestLoadedModel(newModel);

            string serializedModel = newModel.ToString();
            Assert.That(serializedModel, Is.EqualTo(jsonText));
        }

        [Test]
        public void Clone()
        {
            string modelPath = Path.Combine(Directory.GetCurrentDirectory(), "../../../Machine/JSON/model.json");
            string jsonText = System.IO.File.ReadAllText(modelPath);
            using JsonDocument parsedJson = JsonDocument.Parse(jsonText);

            ObjectModel model = new();
            model.UpdateFromJson(parsedJson.RootElement, false);

            ObjectModel newModel = (ObjectModel)model.Clone();
            TestLoadedModel(newModel);

            string serializedModel = newModel.ToString();
            Assert.That(serializedModel, Is.EqualTo(jsonText));
        }
    }
}
