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
        [Test]
        public void UpdateFromJson()
        {
            string modelPath = Path.Combine(Directory.GetCurrentDirectory(), "../../../Machine/JSON/model.json");
            string jsonText = System.IO.File.ReadAllText(modelPath);
            using JsonDocument parsedJson = JsonDocument.Parse(jsonText);

            ObjectModel model = new();

            model.UpdateFromJson(parsedJson.RootElement, false);

            ClassicAssert.IsNotNull(model.State.MessageBox);
            ClassicAssert.AreEqual(MessageBoxMode.OkOnly, model.State.MessageBox?.Mode);
            ClassicAssert.AreEqual("message", model.State.MessageBox?.Message);
            ClassicAssert.AreEqual("title", model.State.MessageBox?.Title);

            string serializedModel = model.ToString();
            ClassicAssert.AreEqual(jsonText, serializedModel);
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

#pragma warning disable CS0618 // Type or member is obsolete
            string patch = updatedModel.MakeStringPatch(modelToUpdate);
#pragma warning restore CS0618 // Type or member is obsolete
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
