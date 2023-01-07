using DuetAPI.ObjectModel;
using DuetControlServer.Model;
using NUnit.Framework;
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

            Assert.AreEqual(1, numEvents);
            Assert.AreEqual(new object[] { "state", "status" }, recordedPath);
            Assert.AreEqual(PropertyChangeType.Property, recordedChangeType);
            Assert.AreEqual(MachineStatus.Halted, recordedValue);

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

            Assert.AreEqual(1, numEvents);
            Assert.AreEqual(new object[] { new ItemPathNode("boards", 0, new object[1]), "v12" }, recordedPath);
            Assert.AreEqual(mainBoard.V12, recordedValue);

            // Reset
            numEvents = 0;
            recordedChangeType = PropertyChangeType.Property;
            recordedValue = null;
            TestContext.Out.WriteLine();

            // Nested item propery 2
            TestContext.Out.WriteLine("Nested item property 2");
            Provider.Get.Inputs.HTTP!.State = InputChannelState.AwaitingAcknowledgement;

            Assert.AreEqual(1, numEvents);
            Assert.AreEqual(new object[] { new ItemPathNode("inputs", (int)DuetAPI.CodeChannel.HTTP, new object[Inputs.Total]), "state" }, recordedPath);
            Assert.AreEqual(InputChannelState.AwaitingAcknowledgement, recordedValue);

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

            Assert.AreEqual(1, numEvents);
            Assert.AreEqual(new object[] { "state", "messageBox" }, recordedPath);
            Assert.AreSame(messageBox, recordedValue);

            // Reset
            numEvents = 0;
            recordedChangeType = PropertyChangeType.Property;
            recordedValue = null;
            TestContext.Out.WriteLine();

            // Replaceable model object property
            TestContext.Out.WriteLine("MessageBox property");
            Provider.Get.State.MessageBox.Message = "asdf";

            Assert.AreEqual(1, numEvents);
            Assert.AreEqual(new object[] { "state", "messageBox", "message" }, recordedPath);
            Assert.AreEqual("asdf", recordedValue);

            // Reset
            numEvents = 0;
            recordedChangeType = PropertyChangeType.Property;
            recordedValue = null;
            TestContext.Out.WriteLine();

            // Replaceable model object 2
            TestContext.Out.WriteLine("Remove MessageBox");
            Provider.Get.State.MessageBox = null;

            Assert.AreEqual(1, numEvents);
            Assert.AreEqual(new object[] { "state", "messageBox" }, recordedPath);
            Assert.AreEqual(null, recordedValue);

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

            Assert.AreEqual(1, numEvents);
            Assert.AreEqual(new object[] { "job", "build" }, recordedPath);
            Assert.AreEqual(PropertyChangeType.Property, recordedChangeType);
            Assert.AreSame(newBuild, recordedValue);

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

            Assert.AreEqual(1, numEvents);
            Assert.AreEqual(new object[] { "plugins", "Foobar" }, recordedPath);
            Assert.AreEqual(PropertyChangeType.Property, recordedChangeType);
            Assert.AreSame(plugin, recordedValue);

            // plugins.foobar.data.test
            JsonElement customData = new();
            plugin.Data["test"] = customData;

            Assert.AreEqual(2, numEvents);
            Assert.AreEqual(new object[] { "plugins", "Foobar", "data", "test" }, recordedPath);
            Assert.AreEqual(PropertyChangeType.Property, recordedChangeType);
            Assert.AreEqual(customData, recordedValue);

            // global.foo
            using JsonDocument jsonDoc = JsonDocument.Parse("{\"foobar\":\"test\"}");
            Provider.Get.Global.Add("foobar", jsonDoc.RootElement);

            Assert.AreEqual(3, numEvents);
            Assert.AreEqual(new object[] { "global", "foobar" }, recordedPath);
            Assert.AreEqual(PropertyChangeType.Property, recordedChangeType);
            Assert.AreEqual(jsonDoc.RootElement, recordedValue);

            // clear event
            Provider.Get.Global.Clear();

            Assert.AreEqual(4, numEvents);
            Assert.AreEqual(new object[] { "global" }, recordedPath);
            Assert.AreEqual(PropertyChangeType.Property, recordedChangeType);
            Assert.IsNull(recordedValue);

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

            Assert.AreEqual(1, numEvents);
            Assert.AreEqual(new object[] { "plugins", "Foobar2" }, recordedPath);
            Assert.AreEqual(PropertyChangeType.Property, recordedChangeType);
            Assert.AreSame(plugin, recordedValue);

            // plugins.foobar.pid
            plugin.Pid = 1234;

            Assert.AreEqual(2, numEvents);
            Assert.AreEqual(new object[] { "plugins", "Foobar2", "pid" }, recordedPath);
            Assert.AreEqual(PropertyChangeType.Property, recordedChangeType);
            Assert.AreEqual(plugin.Pid, recordedValue);

            // delete item
            Provider.Get.Plugins.Remove("Foobar2");

            Assert.AreEqual(3, numEvents);
            Assert.AreEqual(new object[] { "plugins", "Foobar2" }, recordedPath);
            Assert.AreEqual(PropertyChangeType.Property, recordedChangeType);
            Assert.IsNull(recordedValue);

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

            Assert.AreEqual(1, numEvents);
            Assert.AreEqual(PropertyChangeType.Collection, recordedChangeType);
            Assert.AreEqual(new object[] { "heat", new ItemPathNode("heaters", 0, new object[1]) }, recordedPath);
            Assert.AreSame(newHeater, recordedValue);

            // Reset
            numEvents = 0;
            recordedPath = null;
            recordedValue = null;
            TestContext.Out.WriteLine();

            // Add second item
            TestContext.Out.WriteLine("Add item");
            newHeater = new Heater();
            Provider.Get.Heat.Heaters.Add(newHeater);

            Assert.AreEqual(1, numEvents);
            Assert.AreEqual(PropertyChangeType.Collection, recordedChangeType);
            Assert.AreEqual(new object[] { "heat", new ItemPathNode("heaters", 1, new object[2]) }, recordedPath);
            Assert.AreSame(newHeater, recordedValue);

            // Reset
            numEvents = 0;
            recordedPath = null;
            recordedValue = null;
            TestContext.Out.WriteLine();

            // Modify first item
            TestContext.Out.WriteLine("Modify first item");
            Provider.Get.Heat.Heaters[0]!.Active = 123F;

            Assert.AreEqual(1, numEvents);
            Assert.AreEqual(PropertyChangeType.Property, recordedChangeType);
            Assert.AreEqual(new object[] { "heat", new ItemPathNode("heaters", 0, new object[2]), "active" }, recordedPath);
            Assert.AreEqual(123F, recordedValue);

            // Reset
            numEvents = 0;
            recordedPath = null;
            recordedValue = null;
            TestContext.Out.WriteLine();

            // Move item
            TestContext.Out.WriteLine("Move item");
            Provider.Get.Heat.Heaters.Move(1, 0);

            Assert.AreEqual(2, numEvents);
            Assert.AreEqual(PropertyChangeType.Collection, recordedChangeType);
            Assert.AreEqual(new object[] { "heat", new ItemPathNode("heaters", 1, new object[2]) }, recordedPath);
            Assert.AreSame(Provider.Get.Heat.Heaters[1], recordedValue);

            // Reset
            numEvents = 0;
            recordedPath = null;
            recordedValue = null;
            TestContext.Out.WriteLine();

            // Replace item
            TestContext.Out.WriteLine("Replace item");
            newHeater = new Heater { Active = 10F };
            Provider.Get.Heat.Heaters[0] = newHeater;

            Assert.AreEqual(1, numEvents);
            Assert.AreEqual(PropertyChangeType.Collection, recordedChangeType);
            Assert.AreEqual(new object[] { "heat", new ItemPathNode("heaters", 0, new object[2]) }, recordedPath);
            Assert.AreSame(newHeater, recordedValue);

            // Reset
            numEvents = 0;
            recordedPath = null;
            recordedValue = null;
            TestContext.Out.WriteLine();

            // Insert item
            TestContext.Out.WriteLine("Insert item");
            newHeater = new Heater { Standby = 20F };
            Provider.Get.Heat.Heaters.Insert(0, newHeater);

            Assert.AreEqual(3, numEvents);
            Assert.AreEqual(PropertyChangeType.Collection, recordedChangeType);
            Assert.AreEqual(new object[] { "heat", new ItemPathNode("heaters", 2, new object[3]) }, recordedPath);
            Assert.AreEqual(Provider.Get.Heat.Heaters[2], recordedValue);

            // Reset
            numEvents = 0;
            recordedPath = null;
            recordedValue = null;
            TestContext.Out.WriteLine();

            // Remove item
            TestContext.Out.WriteLine("Remove item");
            Provider.Get.Heat.Heaters.RemoveAt(0);

            Assert.AreEqual(3, numEvents);
            Assert.AreEqual(PropertyChangeType.Collection, recordedChangeType);
            Assert.AreEqual(new object[] { "heat", new ItemPathNode("heaters", 1, new object[2]) }, recordedPath);
            Assert.AreEqual(Provider.Get.Heat.Heaters[1], recordedValue);

            // Reset
            numEvents = 0;
            recordedPath = null;
            recordedValue = null;
            TestContext.Out.WriteLine();

            // Clear items
            TestContext.Out.WriteLine("Clear items");
            Provider.Get.Heat.Heaters.Clear();

            Assert.AreEqual(1, numEvents);
            Assert.AreEqual(PropertyChangeType.Collection, recordedChangeType);
            Assert.AreEqual(new object[] { "heat", new ItemPathNode("heaters", 0, Array.Empty<object>()) }, recordedPath);
            Assert.AreEqual(null, recordedValue);

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

            Assert.AreEqual(1, numEvents);
            Assert.AreEqual(new object[] { "messages" }, recordedPath);
            Assert.AreEqual(PropertyChangeType.GrowingCollection, recordedChangeType);
            if (recordedValue is IList list)
            {
                Assert.AreSame(msg, list[0]);
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

            Assert.AreEqual(1, numEvents);
            Assert.AreEqual(PropertyChangeType.GrowingCollection, recordedChangeType);
            Assert.AreEqual(new object[] { "messages" }, recordedPath);
            Assert.AreEqual(null, recordedValue);

            // End
            DuetControlServer.Model.Observer.OnPropertyPathChanged -= onPropertyChanged;
        }
    }
}
