using DuetAPI.ObjectModel;
using NUnit.Framework;
using System.IO;
using System.Text.Json;

namespace UnitTests.Machine
{
    [TestFixture]
    public class Model
    {
        [Test]
        public void UpdateFromJson()
        {
            string modelPath = Path.Combine(Directory.GetCurrentDirectory(), "../../../Machine/JSON/model.json");
            string jsonText = System.IO.File.ReadAllText(modelPath);
            using JsonDocument parsedJson = JsonDocument.Parse(jsonText);

            ObjectModel model = new();

            model.UpdateFromJson(parsedJson.RootElement, false);

            Assert.IsNotNull(model.State.MessageBox);
            Assert.AreEqual(MessageBoxMode.OkOnly, model.State.MessageBox.Mode);
            Assert.AreEqual("message", model.State.MessageBox.Message);
            Assert.AreEqual("title", model.State.MessageBox.Title);

            string serializedModel = model.ToString();
            Assert.AreEqual(jsonText, serializedModel);
        }

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
                Name = "BED2",
                Standby = 20F
            });
            modelToUpdate.Heat.Heaters.Add(new Heater
            {
                Active = 45F,
                Name = "BED3"
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
                Standby = 21F,
                Name = "Bed"
            });
            updatedModel.Heat.Heaters.Add(new Heater
            {
                Name = "BED2",
                Standby = 20F
            });
            updatedModel.Fans.Add(new Fan
            {
                ActualValue = 0.5F,
                RequestedValue = 0.75F
            });
            updatedModel.State.Status = MachineStatus.Pausing;
            updatedModel.Scanner.Status = ScannerStatus.PostProcessing;

            string patch = updatedModel.MakeStringPatch(modelToUpdate);
            TestContext.Out.Write(patch);

            using JsonDocument jsonPatch = JsonDocument.Parse(patch);
            modelToUpdate.UpdateFromJson(jsonPatch.RootElement, false);

            Assert.AreEqual("Yum", modelToUpdate.Boards[0].FirmwareName);
            Assert.AreEqual(2, modelToUpdate.Heat.BedHeaters.Count);
            Assert.AreEqual("Bed", modelToUpdate.Heat.Heaters[0].Name);
            Assert.AreEqual(90F, modelToUpdate.Heat.Heaters[0].Active);
            Assert.AreEqual(21F, modelToUpdate.Heat.Heaters[0].Standby);
            Assert.AreEqual("BED2", modelToUpdate.Heat.Heaters[1].Name);
            Assert.AreEqual(20F, modelToUpdate.Heat.Heaters[1].Standby);
            Assert.AreEqual(1, modelToUpdate.Fans.Count);
            Assert.AreEqual(0.5F, modelToUpdate.Fans[0].ActualValue);
            Assert.AreEqual(0.75F, modelToUpdate.Fans[0].RequestedValue);
            Assert.AreEqual(MachineStatus.Pausing, modelToUpdate.State.Status);
            Assert.AreEqual(ScannerStatus.PostProcessing, modelToUpdate.Scanner.Status);
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
            Assert.AreEqual(jsonText, serializedModel);
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
            Assert.AreEqual(jsonText, serializedModel);
        }

        [Test]
        public void UpdateFromFirmware()
        {
            string modelPath = Path.Combine(Directory.GetCurrentDirectory(), "../../../Machine/JSON/stateKey.json");
            string jsonText = System.IO.File.ReadAllText(modelPath);
            using JsonDocument parsedJson = JsonDocument.Parse(jsonText);

            ObjectModel model = new();
            bool success = model.UpdateFromFirmwareModel("state", parsedJson.RootElement);

            Assert.IsTrue(success);
        }
    }
}
