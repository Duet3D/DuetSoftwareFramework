using DuetAPI.ObjectModel;
using NUnit.Framework;
using NUnit.Framework.Legacy;
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
            Assert.That(model.Sensors.FilamentMonitors[0], Is.TypeOf<FilamentMonitor>());
            Assert.That(model.Sensors.FilamentMonitors[0].Type, Is.EqualTo(FilamentMonitorType.Simple));
        }

        [Test]
        public void UpdateFromJson()
        {
            string modelPath = Path.Combine(Directory.GetCurrentDirectory(), "../../../Machine/JSON/model.json");
            string jsonText = File.ReadAllText(modelPath);
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
            Utf8JsonReader reader = new(File.ReadAllBytes(modelPath));
            reader.Read();

            ObjectModel model = new();
            model.UpdateFromJsonReader(ref reader, false);

            // Test the loaded model values
            TestLoadedModel(model);

            // Serialize OM again and make sure it matches the saved state
            string serializedModel = JsonSerializer.Serialize(model, DuetAPI.Utility.JsonHelper.DefaultJsonOptions);
            Assert.That(serializedModel, Is.EqualTo(File.ReadAllText(modelPath)));
        }


#if false
        [Test]
        public void Patch()
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

            string patch = updatedModel.MakeStringPatch(modelToUpdate);
            TestContext.Out.Write(patch);

            using JsonDocument jsonPatch = JsonDocument.Parse(patch);
            modelToUpdate.UpdateFromJson(jsonPatch.RootElement, false);

            ClassicAssert.AreEqual("Yum", modelToUpdate.Boards[0].FirmwareName);
            ClassicAssert.AreEqual(2, modelToUpdate.Heat.BedHeaters.Count);
            ClassicAssert.AreEqual(90F, modelToUpdate.Heat.Heaters[0]?.Active);
            ClassicAssert.AreEqual(21F, modelToUpdate.Heat.Heaters[0]?.Standby);
            ClassicAssert.AreEqual(20F, modelToUpdate.Heat.Heaters[1]?.Standby);
            ClassicAssert.AreEqual(1, modelToUpdate.Fans.Count);
            ClassicAssert.AreEqual(0.5F, modelToUpdate.Fans[0]?.ActualValue);
            ClassicAssert.AreEqual(0.75F, modelToUpdate.Fans[0]?.RequestedValue);
            ClassicAssert.AreEqual(MachineStatus.Pausing, modelToUpdate.State.Status);
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

            string serializedModel = newModel.ToString();
            ClassicAssert.AreEqual(jsonText, serializedModel);
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

            string serializedModel = newModel.ToString();
            ClassicAssert.AreEqual(jsonText, serializedModel);
        }
#endif

        [Test]
        public void UpdateFromFirmware()
        {
            string modelPath = Path.Combine(Directory.GetCurrentDirectory(), "../../../Machine/JSON/stateKey.json");
            string jsonText = System.IO.File.ReadAllText(modelPath);
            using JsonDocument parsedJson = JsonDocument.Parse(jsonText);

            ObjectModel model = new();
            bool success = model.UpdateFromFirmwareJson("state", parsedJson.RootElement);

            ClassicAssert.IsTrue(success);
        }
    }
}
