using DuetAPI.ObjectModel;
using NUnit.Framework;
using System.IO;
using System.Text;
using System.Text.Json;

namespace UnitTests.Machine
{
    [TestFixture]
    public class Model
    {
        static void TestLoadedModel(ObjectModel model)
        {
            // Test all supported data types
            Assert.That(model.Global["foobar"]!.Value.GetInt32(), Is.EqualTo(123));
            Assert.That(model.Directories.System, Is.EqualTo("0:/sys/custom"));
            Assert.That(model.Move.Axes[0].Letter, Is.EqualTo('W'));
            Assert.That(model.State.AtxPower, Is.True);
            Assert.That(model.Heat.ColdExtrudeTemperature, Is.EqualTo(145F));
            Assert.That(model.Job.FilePosition, Is.EqualTo(12345678));

            // Test nullable ModelObject
            Assert.That(model.State.MessageBox, Is.Not.Null);
            Assert.That(model.State.MessageBox!.Mode, Is.EqualTo(MessageBoxMode.OkOnly));
            Assert.That(model.State.MessageBox!.Message, Is.EqualTo("message"));
            Assert.That(model.State.MessageBox!.Title, Is.EqualTo("title"));

            // Test nullable ModelObject in collection
            Assert.That(model.Heat.Heaters.Count, Is.EqualTo(2));
            Assert.That(model.Heat.Heaters[0], Is.Null);
            Assert.That(model.Heat.Heaters[1]!.Current, Is.EqualTo(25.01F));

            // Test polymorphic ModelObject
            Assert.That(model.Move.Kinematics, Is.TypeOf<CoreKinematics>());
            Assert.That(((CoreKinematics)model.Move.Kinematics).Name, Is.EqualTo(KinematicsName.Cartesian));

            // Test polymorphic ModelObject in collection
            Assert.That(model.Sensors.FilamentMonitors[0], Is.TypeOf<RotatingMagnetFilamentMonitor>());
            Assert.That(model.Sensors.FilamentMonitors[0]!.Type, Is.EqualTo(FilamentMonitorType.RotatingMagnet));
        }

#if false
        [Test]
        public void DerserializeFromJsonDocument()
        {
            string modelPath = Path.Combine(Directory.GetCurrentDirectory(), "../../../Machine/JSON/model.json");
            string jsonText = System.IO.File.ReadAllText(modelPath);
            using JsonDocument parsedJson = JsonDocument.Parse(jsonText);

            ObjectModel model = JsonSerializer.Deserialize(parsedJson, ObjectModelContext.Default.ObjectModel);

            // Test the loaded model values
            TestLoadedModel(model);

            // Serialize OM again and make sure it matches the saved state
            string serializedModel = JsonSerializer.Serialize(model, DuetAPI.Utility.JsonHelper.DefaultJsonOptions);
            Assert.That(serializedModel, Is.EqualTo(jsonText));
        }

        // FIXME Deserialization via standard calls isn't working yet
        [Test]
        public void DerserializeFromJsonReader()
        {
            string modelPath = Path.Combine(Directory.GetCurrentDirectory(), "../../../Machine/JSON/model.json");
            byte[] jsonBytes = System.IO.File.ReadAllBytes(modelPath);

            ObjectModel model = JsonSerializer.Deserialize(jsonBytes, ObjectModelContext.Default.ObjectModel);

            // Test the loaded model values
            TestLoadedModel(model);

            // Serialize OM again and make sure it matches the saved state
            string serializedModel = JsonSerializer.Serialize(model, DuetAPI.Utility.JsonHelper.DefaultJsonOptions);
            Assert.That(serializedModel, Is.EqualTo(Encoding.UTF8.GetString(jsonBytes)));
        }
#endif

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

        [Test]
        public void UpdateFromFirmwareScopes()
        {
            byte[] stateKey = Encoding.UTF8.GetBytes("{\"atxPower\":true}");

            ObjectModel model = new();
            void ResetModel()
            {
                model.Job.LastFileName = "0:/gcodes/test.g";
                model.State.LaserPwm = 0.5F;
                model.State.LogFile = "0:/sys/eventlog.txt";
                model.State.MessageBox = new MessageBox() { Message = "message" };
                model.State.ThisInput = 3;
            }

            // Patches do not reset anything
            ResetModel();
            Utf8JsonReader reader = new(stateKey);
            reader.Read();
            Assert.That(model.UpdateFromFirmwareJsonReader("state", ref reader), Is.True);
            Assert.That(model.State.LaserPwm, Is.EqualTo(0.5F));
            Assert.That(model.State.MessageBox, Is.Not.Null);

            // Live updates only reset live properties
            ResetModel();
            reader = new(stateKey);
            reader.Read();
            Assert.That(model.UpdateFromFirmwareJsonReader("state", ref reader, 0, true, ModelUpdateScope.Live), Is.True);
            Assert.That(model.State.LaserPwm, Is.Null);
            Assert.That(model.State.MessageBox, Is.Not.Null);

            // Full updates reset the key they contain but leave other keys, SBC and verbose properties alone
            ResetModel();
            reader = new(stateKey);
            reader.Read();
            Assert.That(model.UpdateFromFirmwareJsonReader("state", ref reader, 0, true, ModelUpdateScope.Full), Is.True);
            Assert.That(model.State.LaserPwm, Is.Null);
            Assert.That(model.State.MessageBox, Is.Null);
            Assert.That(model.State.LogFile, Is.EqualTo("0:/sys/eventlog.txt"));
            Assert.That(model.State.ThisInput, Is.EqualTo(3));
            Assert.That(model.Job.LastFileName, Is.EqualTo("0:/gcodes/test.g"));

            // Verbose properties are reset as well if the query asked for them
            ResetModel();
            reader = new(stateKey);
            reader.Read();
            Assert.That(model.UpdateFromFirmwareJsonReader("state", ref reader, 0, true, ModelUpdateScope.Full | ModelUpdateScope.Verbose), Is.True);
            Assert.That(model.State.ThisInput, Is.Null);
        }

        [Test]
        public void BoardTypes()
        {
            ObjectModel model = new();
            Utf8JsonReader reader = new(Encoding.UTF8.GetBytes("[{\"canAddress\":0,\"firmwareName\":\"RepRapFirmware for Duet 3 MB6HC\"},{\"canAddress\":1,\"state\":\"running\"}]"));
            reader.Read();
            Assert.That(model.UpdateFromFirmwareJsonReader("boards", ref reader), Is.True);

            Assert.That(model.Boards[0], Is.TypeOf<MainBoard>());
            Assert.That(((MainBoard)model.Boards[0]).FirmwareName, Is.EqualTo("RepRapFirmware for Duet 3 MB6HC"));
            Assert.That(model.Boards[1], Is.TypeOf<ExpansionBoard>());
            Assert.That(((ExpansionBoard)model.Boards[1]).State, Is.EqualTo(BoardState.Running));

            // Live payloads carry nothing to tell the board types apart, so the classes must survive them
            reader = new(Encoding.UTF8.GetBytes("[{\"freeRam\":1234},{\"freeRam\":5678}]"));
            reader.Read();
            Assert.That(model.UpdateFromFirmwareJsonReader("boards", ref reader, 0, true, ModelUpdateScope.Live), Is.True);
            Assert.That(model.Boards[0], Is.TypeOf<MainBoard>());
            Assert.That(model.Boards[1], Is.TypeOf<ExpansionBoard>());
            Assert.That(model.Boards[0].FreeRam, Is.EqualTo(1234));

            // Both derived types must serialize their own properties
            string json = JsonSerializer.Serialize(model.Boards, DuetAPI.Utility.JsonHelper.DefaultJsonOptions);
            Assert.That(json, Does.Contain("\"firmwareName\":\"RepRapFirmware for Duet 3 MB6HC\""));
            Assert.That(json, Does.Contain("\"state\":\"running\""));
        }

        private static readonly int[][] expectedBedHeaterMapping = [[0], [1]];

        [Test]
        public void UpdateFromOther()
        {
            ObjectModel modelToUpdate = new();
            modelToUpdate.Boards.Add(new MainBoard
            {
                FirmwareName = "Foobar"
            });
            modelToUpdate.Heat.BedHeaterMapping.Add([-1]);
            modelToUpdate.Heat.BedHeaterMapping.Add([1]);
            modelToUpdate.Heat.BedHeaterMapping.Add([2]);
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
            updatedModel.Boards.Add(new MainBoard
            {
                FirmwareName = "Yum"
            });
            updatedModel.Heat.BedHeaterMapping.Add([0]);
            updatedModel.Heat.BedHeaterMapping.Add([1]);
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

            Assert.That(((MainBoard)modelToUpdate.Boards[0]!).FirmwareName, Is.EqualTo("Yum"));
            Assert.That(modelToUpdate.Heat.BedHeaterMapping, Is.EquivalentTo(expectedBedHeaterMapping));
            Assert.That(modelToUpdate.Heat.Heaters[0]!.Active, Is.EqualTo(90F));
            Assert.That(modelToUpdate.Heat.Heaters[0]!.Standby, Is.EqualTo(21F));
            Assert.That(modelToUpdate.Heat.Heaters[1]!.Standby, Is.EqualTo(20F));
            Assert.That(modelToUpdate.Fans[0]!.ActualValue, Is.EqualTo(0.5F));
            Assert.That(modelToUpdate.Fans[0]!.RequestedValue, Is.EqualTo(0.75F));
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

            Assert.That(((MainBoard)model.Boards[0]).FirmwareName, Is.EqualTo("Test"));
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
        public void AssignKinematicsTypeChange()
        {
            ObjectModel cartesianModel = new(), deltaModel = new();
            using (JsonDocument json = JsonDocument.Parse("{\"move\":{\"kinematics\":{\"name\":\"cartesian\"}}}"))
            {
                cartesianModel.UpdateFromJson(json.RootElement, false);
            }
            using (JsonDocument json = JsonDocument.Parse("{\"move\":{\"kinematics\":{\"name\":\"delta\"}}}"))
            {
                deltaModel.UpdateFromJson(json.RootElement, false);
            }
            Assert.That(cartesianModel.Move.Kinematics, Is.TypeOf<CoreKinematics>());
            Assert.That(deltaModel.Move.Kinematics, Is.TypeOf<DeltaKinematics>());

            // Assigning a model whose kinematics type differs must replace the instance, not crash
            cartesianModel.Assign(deltaModel);
            Assert.That(cartesianModel.Move.Kinematics, Is.TypeOf<DeltaKinematics>());
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
