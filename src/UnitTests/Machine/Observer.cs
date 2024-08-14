#if false
using DuetAPI.ObjectModel;
using DuetControlServer.Model;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using System;
using System.Collections;
using System.Text.Json;

namespace UnitTests.Machine
{
    [TestFixture]
    public class Observer
    {
        [OneTimeSetUp]
        public void Setup()
        {
            DuetControlServer.Model.Observer.Init();
        }

        [Test]
        public void ObserveProperty()
        {
            int numEvents = 0;
            object[]? recordedPath = null;
            PropertyChangeType recordedChangeType = PropertyChangeType.Property;
            object? recordedValue = null;
            void onPropertyChanged(object[] path, PropertyChangeType changeType, object? value)
            {
                numEvents++;
                recordedChangeType = changeType;
                recordedPath = path;
                recordedValue = value;

                TestContext.Out.WriteLine("Change {0} ({1}) -> {2}", string.Join('.', path), changeType, value);
            }
            DuetControlServer.Model.Observer.OnPropertyPathChanged += onPropertyChanged;

            // Simple property
            TestContext.Out.WriteLine("Simple property");
            Provider.Get.State.Status = MachineStatus.Halted;

            ClassicAssert.AreEqual(1, numEvents);
            ClassicAssert.AreEqual(new object[] { "state", "status" }, recordedPath);
            ClassicAssert.AreEqual(PropertyChangeType.Property, recordedChangeType);
            ClassicAssert.AreEqual(MachineStatus.Halted, recordedValue);

            // Nested item property
            TestContext.Out.WriteLine("Adding new item");
            Board mainBoard = new();
            Provider.Get.Boards.Add(mainBoard);

            // Reset
            numEvents = 0;
            recordedChangeType = PropertyChangeType.Property;
            recordedValue = null;
            TestContext.Out.WriteLine();

            // Nested item propery
            TestContext.Out.WriteLine("Nested item property");
            mainBoard.V12 = new() { Current = 123F };

            ClassicAssert.AreEqual(1, numEvents);
            ClassicAssert.AreEqual(new object[] { new ItemPathNode("boards", 0, new object[1]), "v12" }, recordedPath);
            ClassicAssert.AreEqual(mainBoard.V12, recordedValue);

            // Reset
            numEvents = 0;
            recordedChangeType = PropertyChangeType.Property;
            recordedValue = null;
            TestContext.Out.WriteLine();

            // Nested item propery 2
            TestContext.Out.WriteLine("Nested item property 2");
            Provider.Get.Inputs.HTTP!.State = InputChannelState.AwaitingAcknowledgement;

            ClassicAssert.AreEqual(1, numEvents);
            ClassicAssert.AreEqual(new object[] { new ItemPathNode("inputs", (int)DuetAPI.CodeChannel.HTTP, new object[Inputs.Total]), "state" }, recordedPath);
            ClassicAssert.AreEqual(InputChannelState.AwaitingAcknowledgement, recordedValue);

            // Reset
            numEvents = 0;
            recordedChangeType = PropertyChangeType.Property;
            recordedValue = null;
            TestContext.Out.WriteLine();

            // Replaceable model object
            TestContext.Out.WriteLine("Assign MessageBox");
            MessageBox messageBox = new()
            {
                Message = "Test",
                Mode = MessageBoxMode.OkCancel,
                Title = "Test title"
            };
            Provider.Get.State.MessageBox = messageBox;

            ClassicAssert.AreEqual(1, numEvents);
            ClassicAssert.AreEqual(new object[] { "state", "messageBox" }, recordedPath);
            ClassicAssert.AreSame(messageBox, recordedValue);

            // Reset
            numEvents = 0;
            recordedChangeType = PropertyChangeType.Property;
            recordedValue = null;
            TestContext.Out.WriteLine();

            // Replaceable model object property
            TestContext.Out.WriteLine("MessageBox property");
            Provider.Get.State.MessageBox.Message = "asdf";

            ClassicAssert.AreEqual(1, numEvents);
            ClassicAssert.AreEqual(new object[] { "state", "messageBox", "message" }, recordedPath);
            ClassicAssert.AreEqual("asdf", recordedValue);

            // Reset
            numEvents = 0;
            recordedChangeType = PropertyChangeType.Property;
            recordedValue = null;
            TestContext.Out.WriteLine();

            // Replaceable model object 2
            TestContext.Out.WriteLine("Remove MessageBox");
            Provider.Get.State.MessageBox = null;

            ClassicAssert.AreEqual(1, numEvents);
            ClassicAssert.AreEqual(new object[] { "state", "messageBox" }, recordedPath);
            ClassicAssert.AreEqual(null, recordedValue);

            // End
            DuetControlServer.Model.Observer.OnPropertyPathChanged -= onPropertyChanged;
        }

        [Test]
        public void ObserveModelProperty()
        {
            int numEvents = 0;
            object[]? recordedPath = null;
            PropertyChangeType recordedChangeType = PropertyChangeType.Property;
            object? recordedValue = null;
            void onPropertyChanged(object[] path, PropertyChangeType changeType, object? value)
            {
                numEvents++;
                recordedChangeType = changeType;
                recordedPath = path;
                recordedValue = value;

                TestContext.Out.WriteLine("Change {0} ({1}) -> {2}", string.Join('.', path), changeType, value);
            }
            DuetControlServer.Model.Observer.OnPropertyPathChanged += onPropertyChanged;

            // job.build[]
            Build newBuild = new();
            Provider.Get.Job.Build = newBuild;

            ClassicAssert.AreEqual(1, numEvents);
            ClassicAssert.AreEqual(new object[] { "job", "build" }, recordedPath);
            ClassicAssert.AreEqual(PropertyChangeType.Property, recordedChangeType);
            ClassicAssert.AreSame(newBuild, recordedValue);

            // End
            DuetControlServer.Model.Observer.OnPropertyPathChanged -= onPropertyChanged;
        }

        [Test]
        public void ObserveModelDictionary()
        {
            int numEvents = 0;
            object[]? recordedPath = null;
            PropertyChangeType recordedChangeType = PropertyChangeType.Property;
            object? recordedValue = null;
            void onPropertyChanged(object[] path, PropertyChangeType changeType, object? value)
            {
                numEvents++;
                recordedChangeType = changeType;
                recordedPath = path;
                recordedValue = value;

                TestContext.Out.WriteLine("Change {0} ({1}) -> {2}", string.Join('.', path), changeType, value);
            }
            DuetControlServer.Model.Observer.OnPropertyPathChanged += onPropertyChanged;

            // plugins
            Plugin plugin = new() { Id = "Foobar" };
            Provider.Get.Plugins.Add("Foobar", plugin);

            ClassicAssert.AreEqual(1, numEvents);
            ClassicAssert.AreEqual(new object[] { "plugins", "Foobar" }, recordedPath);
            ClassicAssert.AreEqual(PropertyChangeType.Property, recordedChangeType);
            ClassicAssert.AreSame(plugin, recordedValue);

            // plugins.foobar.data.test
            JsonElement customData = JsonSerializer.SerializeToElement(new { A = 1, B = "foo" });
            plugin.Data["test"] = customData;

            ClassicAssert.AreEqual(2, numEvents);
            ClassicAssert.AreEqual(new object[] { "plugins", "Foobar", "data", "test" }, recordedPath);
            ClassicAssert.AreEqual(PropertyChangeType.Property, recordedChangeType);
            ClassicAssert.IsTrue(recordedValue is JsonElement recordedElem && customData.GetRawText().Equals(recordedElem.GetRawText()));

            // global.foo
            using JsonDocument jsonDoc = JsonDocument.Parse("{\"foobar\":\"test\"}");
            Provider.Get.Global.Add("foobar", jsonDoc.RootElement);

            ClassicAssert.AreEqual(3, numEvents);
            ClassicAssert.AreEqual(new object[] { "global", "foobar" }, recordedPath);
            ClassicAssert.AreEqual(PropertyChangeType.Property, recordedChangeType);
            ClassicAssert.IsTrue(recordedValue is JsonElement otherRecordedElem && jsonDoc.RootElement.GetRawText().Equals(otherRecordedElem.GetRawText()));

            // clear event
            Provider.Get.Global.Clear();

            ClassicAssert.AreEqual(4, numEvents);
            ClassicAssert.AreEqual(new object[] { "global" }, recordedPath);
            ClassicAssert.AreEqual(PropertyChangeType.Property, recordedChangeType);
            ClassicAssert.IsNull(recordedValue);

            // End
            DuetControlServer.Model.Observer.OnPropertyPathChanged -= onPropertyChanged;
            Provider.Get.Plugins.Clear();
        }

        [Test]
        public void ObserveModelObjectDictionary()
        {
            int numEvents = 0;
            object[]? recordedPath = null;
            PropertyChangeType recordedChangeType = PropertyChangeType.Property;
            object? recordedValue = null;
            void onPropertyChanged(object[] path, PropertyChangeType changeType, object? value)
            {
                numEvents++;
                recordedChangeType = changeType;
                recordedPath = path;
                recordedValue = value;

                TestContext.Out.WriteLine("Change {0} ({1}) -> {2}", string.Join('.', path), changeType, value);
            }
            DuetControlServer.Model.Observer.OnPropertyPathChanged += onPropertyChanged;

            // plugins
            Plugin plugin = new() { Id = "Foobar2" };
            Provider.Get.Plugins.Add("Foobar2", plugin);

            ClassicAssert.AreEqual(1, numEvents);
            ClassicAssert.AreEqual(new object[] { "plugins", "Foobar2" }, recordedPath);
            ClassicAssert.AreEqual(PropertyChangeType.Property, recordedChangeType);
            ClassicAssert.AreSame(plugin, recordedValue);

            // plugins.foobar.pid
            plugin.Pid = 1234;

            ClassicAssert.AreEqual(2, numEvents);
            ClassicAssert.AreEqual(new object[] { "plugins", "Foobar2", "pid" }, recordedPath);
            ClassicAssert.AreEqual(PropertyChangeType.Property, recordedChangeType);
            ClassicAssert.AreEqual(plugin.Pid, recordedValue);

            // delete item
            Provider.Get.Plugins.Remove("Foobar2");

            ClassicAssert.AreEqual(3, numEvents);
            ClassicAssert.AreEqual(new object[] { "plugins", "Foobar2" }, recordedPath);
            ClassicAssert.AreEqual(PropertyChangeType.Property, recordedChangeType);
            ClassicAssert.IsNull(recordedValue);

            // End
            DuetControlServer.Model.Observer.OnPropertyPathChanged -= onPropertyChanged;
            Provider.Get.Plugins.Clear();
        }

        [Test]
        public void ObserveModelObjectCollection()
        {
            int numEvents = 0;
            PropertyChangeType recordedChangeType = PropertyChangeType.Property;
            object[]? recordedPath = null;
            object? recordedValue = null;
            void onPropertyChanged(object[] path, PropertyChangeType changeType, object? value)
            {
                numEvents++;
                recordedChangeType = changeType;
                recordedPath = path;
                recordedValue = value;

                TestContext.Out.WriteLine("Change {0} ({1}) -> {2}", string.Join('.', path), changeType, value);
            }
            DuetControlServer.Model.Observer.OnPropertyPathChanged += onPropertyChanged;

            // Add first item
            TestContext.Out.WriteLine("Add item");
            Heater newHeater = new();
            Provider.Get.Heat.Heaters.Add(newHeater);

            ClassicAssert.AreEqual(1, numEvents);
            ClassicAssert.AreEqual(PropertyChangeType.Collection, recordedChangeType);
            ClassicAssert.AreEqual(new object[] { "heat", new ItemPathNode("heaters", 0, new object[1]) }, recordedPath);
            ClassicAssert.AreSame(newHeater, recordedValue);

            // Reset
            numEvents = 0;
            recordedPath = null;
            recordedValue = null;
            TestContext.Out.WriteLine();

            // Add second item
            TestContext.Out.WriteLine("Add item");
            newHeater = new Heater();
            Provider.Get.Heat.Heaters.Add(newHeater);

            ClassicAssert.AreEqual(1, numEvents);
            ClassicAssert.AreEqual(PropertyChangeType.Collection, recordedChangeType);
            ClassicAssert.AreEqual(new object[] { "heat", new ItemPathNode("heaters", 1, new object[2]) }, recordedPath);
            ClassicAssert.AreSame(newHeater, recordedValue);

            // Reset
            numEvents = 0;
            recordedPath = null;
            recordedValue = null;
            TestContext.Out.WriteLine();

            // Modify first item
            TestContext.Out.WriteLine("Modify first item");
            Provider.Get.Heat.Heaters[0]!.Active = 123F;

            ClassicAssert.AreEqual(1, numEvents);
            ClassicAssert.AreEqual(PropertyChangeType.Property, recordedChangeType);
            ClassicAssert.AreEqual(new object[] { "heat", new ItemPathNode("heaters", 0, new object[2]), "active" }, recordedPath);
            ClassicAssert.AreEqual(123F, recordedValue);

            // Reset
            numEvents = 0;
            recordedPath = null;
            recordedValue = null;
            TestContext.Out.WriteLine();

            // Move item
            TestContext.Out.WriteLine("Move item");
            Provider.Get.Heat.Heaters.Move(1, 0);

            ClassicAssert.AreEqual(2, numEvents);
            ClassicAssert.AreEqual(PropertyChangeType.Collection, recordedChangeType);
            ClassicAssert.AreEqual(new object[] { "heat", new ItemPathNode("heaters", 1, new object[2]) }, recordedPath);
            ClassicAssert.AreSame(Provider.Get.Heat.Heaters[1], recordedValue);

            // Reset
            numEvents = 0;
            recordedPath = null;
            recordedValue = null;
            TestContext.Out.WriteLine();

            // Replace item
            TestContext.Out.WriteLine("Replace item");
            newHeater = new Heater { Active = 10F };
            Provider.Get.Heat.Heaters[0] = newHeater;

            ClassicAssert.AreEqual(1, numEvents);
            ClassicAssert.AreEqual(PropertyChangeType.Collection, recordedChangeType);
            ClassicAssert.AreEqual(new object[] { "heat", new ItemPathNode("heaters", 0, new object[2]) }, recordedPath);
            ClassicAssert.AreSame(newHeater, recordedValue);

            // Reset
            numEvents = 0;
            recordedPath = null;
            recordedValue = null;
            TestContext.Out.WriteLine();

            // Insert item
            TestContext.Out.WriteLine("Insert item");
            newHeater = new Heater { Standby = 20F };
            Provider.Get.Heat.Heaters.Insert(0, newHeater);

            ClassicAssert.AreEqual(3, numEvents);
            ClassicAssert.AreEqual(PropertyChangeType.Collection, recordedChangeType);
            ClassicAssert.AreEqual(new object[] { "heat", new ItemPathNode("heaters", 2, new object[3]) }, recordedPath);
            ClassicAssert.AreEqual(Provider.Get.Heat.Heaters[2], recordedValue);

            // Reset
            numEvents = 0;
            recordedPath = null;
            recordedValue = null;
            TestContext.Out.WriteLine();

            // Remove item
            TestContext.Out.WriteLine("Remove item");
            Provider.Get.Heat.Heaters.RemoveAt(0);

            ClassicAssert.AreEqual(3, numEvents);
            ClassicAssert.AreEqual(PropertyChangeType.Collection, recordedChangeType);
            ClassicAssert.AreEqual(new object[] { "heat", new ItemPathNode("heaters", 1, new object[2]) }, recordedPath);
            ClassicAssert.AreEqual(Provider.Get.Heat.Heaters[1], recordedValue);

            // Reset
            numEvents = 0;
            recordedPath = null;
            recordedValue = null;
            TestContext.Out.WriteLine();

            // Clear items
            TestContext.Out.WriteLine("Clear items");
            Provider.Get.Heat.Heaters.Clear();

            ClassicAssert.AreEqual(1, numEvents);
            ClassicAssert.AreEqual(PropertyChangeType.Collection, recordedChangeType);
            ClassicAssert.AreEqual(new object[] { "heat", new ItemPathNode("heaters", 0, Array.Empty<object>()) }, recordedPath);
            ClassicAssert.AreEqual(null, recordedValue);

            // End
            DuetControlServer.Model.Observer.OnPropertyPathChanged -= onPropertyChanged;
        }

        [Test]
        public void ObserveModelGrowingCollectiion()
        {
            int numEvents = 0;
            object[]? recordedPath = null;
            PropertyChangeType recordedChangeType = PropertyChangeType.Property;
            object? recordedValue = null;
            void onPropertyChanged(object[] path, PropertyChangeType changeType, object? value)
            {
                numEvents++;
                recordedChangeType = changeType;
                recordedPath = path;
                recordedValue = value;

                TestContext.Out.WriteLine("Change {0} ({1}) -> {2}", string.Join('.', path), changeType, value);
            }
            DuetControlServer.Model.Observer.OnPropertyPathChanged += onPropertyChanged;

            // Add item
            TestContext.Out.WriteLine("Add item");
            Message msg = new(MessageType.Success, "TEST");
            Provider.Get.Messages.Add(msg);

            ClassicAssert.AreEqual(1, numEvents);
            ClassicAssert.AreEqual(new object[] { "messages" }, recordedPath);
            ClassicAssert.AreEqual(PropertyChangeType.GrowingCollection, recordedChangeType);
            if (recordedValue is IList list)
            {
                ClassicAssert.AreSame(msg, list[0]);
            }
            else
            {
                Assert.Fail("Invalid change value type");
            }

            // Reset
            numEvents = 0;
            recordedPath = null;
            recordedValue = null;
            TestContext.Out.WriteLine();

            // Clear items
            TestContext.Out.WriteLine("Clear items");
            Provider.Get.Messages.Clear();

            ClassicAssert.AreEqual(1, numEvents);
            ClassicAssert.AreEqual(PropertyChangeType.GrowingCollection, recordedChangeType);
            ClassicAssert.AreEqual(new object[] { "messages" }, recordedPath);
            ClassicAssert.AreEqual(null, recordedValue);

            // End
            DuetControlServer.Model.Observer.OnPropertyPathChanged -= onPropertyChanged;
        }
    }
}
#endif
