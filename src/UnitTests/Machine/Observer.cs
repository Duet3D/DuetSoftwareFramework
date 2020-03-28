using DuetAPI.Machine;
using DuetControlServer.Model;
using NUnit.Framework;
using System.Collections;

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
            object[] recordedPath = null;
            PropertyChangeType recordedChangeType = PropertyChangeType.Property;
            object recordedValue = null;
            void onPropertyChanged(object[] path, PropertyChangeType changeType, object value)
            {
                numEvents++;
                recordedChangeType = changeType;
                recordedPath = path;
                recordedValue = value;

                TestContext.Out.WriteLine("Change {0} ({1}) -> {2}", string.Join('.', path), changeType, value);
            }
            DuetControlServer.Model.Observer.OnPropertyPathChanged += onPropertyChanged;

            Provider.Get.State.Status = MachineStatus.Halted;

            Assert.AreEqual(1, numEvents);
            Assert.AreEqual(new object[] { "state", "status" }, recordedPath);
            Assert.AreEqual(PropertyChangeType.Property, recordedChangeType);
            Assert.AreEqual(MachineStatus.Halted, recordedValue);

            // End
            DuetControlServer.Model.Observer.OnPropertyPathChanged -= onPropertyChanged;
        }

        [Test]
        public void ObserveModelProperty()
        {
            int numEvents = 0;
            object[] recordedPath = null;
            PropertyChangeType recordedChangeType = PropertyChangeType.Property;
            object recordedValue = null;
            void onPropertyChanged(object[] path, PropertyChangeType changeType, object value)
            {
                numEvents++;
                recordedChangeType = changeType;
                recordedPath = path;
                recordedValue = value;

                TestContext.Out.WriteLine("Change {0} ({1}) -> {2}", string.Join('.', path), changeType, value);
            }
            DuetControlServer.Model.Observer.OnPropertyPathChanged += onPropertyChanged;

            Build newBuild = new Build();
            Provider.Get.Job.Build = newBuild;

            Assert.AreEqual(1, numEvents);
            Assert.AreEqual(new object[] { "job", "build" }, recordedPath);
            Assert.AreEqual(PropertyChangeType.Property, recordedChangeType);
            Assert.AreSame(newBuild, recordedValue);

            // End
            DuetControlServer.Model.Observer.OnPropertyPathChanged -= onPropertyChanged;
        }

        [Test]
        public void ObserveModelObjectCollection()
        {
            int numEvents = 0;
            PropertyChangeType recordedChangeType = PropertyChangeType.Property;
            object[] recordedPath = null;
            object recordedValue = null;
            void onPropertyChanged(object[] path, PropertyChangeType changeType, object value)
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
            Heater newHeater = new Heater();
            Provider.Get.Heat.Heaters.Add(newHeater);

            Assert.AreEqual(1, numEvents);
            Assert.AreEqual(PropertyChangeType.ObjectCollection, recordedChangeType);
            Assert.AreEqual(new object[] { "heat", new ItemPathNode("heaters", 0, 1) }, recordedPath);
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
            Assert.AreEqual(PropertyChangeType.ObjectCollection, recordedChangeType);
            Assert.AreEqual(new object[] { "heat", new ItemPathNode("heaters", 1, 2) }, recordedPath);
            Assert.AreSame(newHeater, recordedValue);

            // Reset
            numEvents = 0;
            recordedPath = null;
            recordedValue = null;
            TestContext.Out.WriteLine();

            // Move item
            TestContext.Out.WriteLine("Move item");
            Provider.Get.Heat.Heaters.Move(1, 0);

            Assert.AreEqual(2, numEvents);
            Assert.AreEqual(PropertyChangeType.ObjectCollection, recordedChangeType);
            Assert.AreEqual(new object[] { "heat", new ItemPathNode("heaters", 1, 2) }, recordedPath);
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
            Assert.AreEqual(PropertyChangeType.ObjectCollection, recordedChangeType);
            Assert.AreEqual(new object[] { "heat", new ItemPathNode("heaters", 0, 2) }, recordedPath);
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
            Assert.AreEqual(PropertyChangeType.ObjectCollection, recordedChangeType);
            Assert.AreEqual(new object[] { "heat", new ItemPathNode("heaters", 2, 3) }, recordedPath);
            Assert.AreEqual(Provider.Get.Heat.Heaters[2], recordedValue);

            // Reset
            numEvents = 0;
            recordedPath = null;
            recordedValue = null;
            TestContext.Out.WriteLine();

            // Remove item
            TestContext.Out.WriteLine("Remove item");
            Provider.Get.Heat.Heaters.RemoveAt(0);

            Assert.AreEqual(2, numEvents);
            Assert.AreEqual(PropertyChangeType.ObjectCollection, recordedChangeType);
            Assert.AreEqual(new object[] { "heat", new ItemPathNode("heaters", 1, 2) }, recordedPath);
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
            Assert.AreEqual(PropertyChangeType.ObjectCollection, recordedChangeType);
            Assert.AreEqual(new object[] { "heat", new ItemPathNode("heaters", 0, 0) }, recordedPath);
            Assert.AreEqual(null, recordedValue);

            // End
            DuetControlServer.Model.Observer.OnPropertyPathChanged -= onPropertyChanged;
        }

        [Test]
        public void ObserveModelGrowingCollectiion()
        {
            int numEvents = 0;
            object[] recordedPath = null;
            PropertyChangeType recordedChangeType = PropertyChangeType.Property;
            object recordedValue = null;
            void onPropertyChanged(object[] path, PropertyChangeType changeType, object value)
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
            Message msg = new Message(MessageType.Success, "TEST");
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
