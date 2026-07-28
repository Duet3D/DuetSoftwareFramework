using DuetAPI.ObjectModel;
using DuetControlServer;
using DuetControlServer.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using DcsModel = DuetControlServer.Model.ObjectModel;
using DcsObserver = DuetControlServer.Model.Observer;

namespace UnitTests.Machine
{
    [TestFixture]
    public class Observer
    {
        private sealed class TestLifetime : IHostApplicationLifetime
        {
            public CancellationToken ApplicationStarted => CancellationToken.None;
            public CancellationToken ApplicationStopping => CancellationToken.None;
            public CancellationToken ApplicationStopped => CancellationToken.None;
            public void StopApplication() { }
        }

        private DcsModel _model = null!;
        private readonly List<(object[] Path, PropertyChangeType Type, object? Value)> _changes = [];

        [SetUp]
        public void Setup()
        {
            _changes.Clear();
            _model = new DcsModel(new TestLifetime(), NullLogger<DcsModel>.Instance, Options.Create(new Settings()));

            DcsObserver observer = new(_model);
            observer.OnPropertyPathChanged += (path, changeType, value) => _changes.Add((path, changeType, value));
            observer.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        private static string PathToString(object[] path) => string.Join('/', path.Select(item => item.ToString()));

        private void AssertSingleChange(string path, PropertyChangeType changeType, object? value)
        {
            Assert.That(_changes, Has.Count.EqualTo(1), $"expected exactly one change for {path}");
            Assert.That(PathToString(_changes[0].Path), Is.EqualTo(path));
            Assert.That(_changes[0].Type, Is.EqualTo(changeType));
            if (value is null || value.GetType().IsValueType)
            {
                Assert.That(_changes[0].Value, Is.EqualTo(value));
            }
            else
            {
                Assert.That(_changes[0].Value, Is.SameAs(value));
            }
        }

        [Test]
        public void ObserveProperty()
        {
            _model.Heat.ColdExtrudeTemperature = 145F;
            AssertSingleChange("heat/coldExtrudeTemperature", PropertyChangeType.Property, 145F);

            _changes.Clear();
            _model.Network.Hostname = "duet3";
            AssertSingleChange("network/hostname", PropertyChangeType.Property, "duet3");
        }

        [Test]
        public void ObserveReplacedModelObject()
        {
            MessageBox first = new() { Message = "first" };
            _model.State.MessageBox = first;
            AssertSingleChange("state/messageBox", PropertyChangeType.Property, first);

            _changes.Clear();
            first.Message = "changed";
            AssertSingleChange("state/messageBox/message", PropertyChangeType.Property, "changed");

            MessageBox second = new() { Message = "second" };
            _changes.Clear();
            _model.State.MessageBox = second;
            AssertSingleChange("state/messageBox", PropertyChangeType.Property, second);

            // The replaced instance must have been unsubscribed from, else its events leak into the new subscription
            _changes.Clear();
            first.Message = "stale";
            Assert.That(_changes, Is.Empty);

            second.Message = "updated";
            AssertSingleChange("state/messageBox/message", PropertyChangeType.Property, "updated");
        }

        [Test]
        public void ObserveNullTransitions()
        {
            MessageBox box = new();
            _model.State.MessageBox = box;
            AssertSingleChange("state/messageBox", PropertyChangeType.Property, box);

            _changes.Clear();
            _model.State.MessageBox = null;
            AssertSingleChange("state/messageBox", PropertyChangeType.Property, null);

            _changes.Clear();
            _model.State.MessageBox = box;
            AssertSingleChange("state/messageBox", PropertyChangeType.Property, box);

            _changes.Clear();
            box.Message = "back again";
            AssertSingleChange("state/messageBox/message", PropertyChangeType.Property, "back again");
        }

        [Test]
        public void ObserveModelCollection()
        {
            Heater heater = new();
            _model.Heat.Heaters.Add(heater);
            AssertSingleChange("heat/heaters[0 of 1]", PropertyChangeType.Collection, heater);

            _changes.Clear();
            heater.Current = 25.01F;
            AssertSingleChange("heat/heaters[0 of 1]/current", PropertyChangeType.Property, 25.01F);

            _changes.Clear();
            _model.Heat.Heaters.RemoveAt(0);
            AssertSingleChange("heat/heaters[0 of 0]", PropertyChangeType.Collection, null);

            _model.Heat.Heaters.Add(new Heater());
            _model.Heat.Heaters.Add(new Heater());
            _changes.Clear();
            _model.Heat.Heaters.Clear();
            AssertSingleChange("heat/heaters[0 of 0]", PropertyChangeType.Collection, null);
        }

        [Test]
        public void ObserveModelDictionary()
        {
            _model.Global["foo"] = JsonDocument.Parse("123").RootElement.Clone();
            Assert.That(_changes, Has.Count.EqualTo(1));
            Assert.That(PathToString(_changes[0].Path), Is.EqualTo("global/foo"));
            Assert.That(_changes[0].Type, Is.EqualTo(PropertyChangeType.Property));
            Assert.That(((JsonElement)_changes[0].Value!).GetInt32(), Is.EqualTo(123));

            _changes.Clear();
            _model.Global.Clear();
            AssertSingleChange("global", PropertyChangeType.Property, null);
        }

        [Test]
        public void ObserveObservableCollection()
        {
            int[] mapping = [0, 1];
            _model.Heat.BedHeaterMapping.Add(mapping);
            AssertSingleChange("heat/bedHeaterMapping[0 of 1]", PropertyChangeType.Collection, mapping);

            _changes.Clear();
            _model.Heat.BedHeaterMapping.RemoveAt(0);
            AssertSingleChange("heat/bedHeaterMapping[0 of 0]", PropertyChangeType.Collection, null);
        }

        [Test]
        public void UnknownPropertiesAreNotReported()
        {
            FieldInfo? eventField = typeof(ModelObject).GetField("PropertyChanged", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(eventField, Is.Not.Null);

            PropertyChangedEventHandler? handler = (PropertyChangedEventHandler?)eventField!.GetValue(_model.Heat);
            Assert.That(handler, Is.Not.Null, "observer did not subscribe to heat");

            // Properties outside the DuetAPI object model must never be reported to clients
            handler!(_model.Heat, new PropertyChangedEventArgs("NotAModelProperty"));
            Assert.That(_changes, Is.Empty);

            handler!(_model.Heat, new PropertyChangedEventArgs("ColdExtrudeTemperature"));
            AssertSingleChange("heat/coldExtrudeTemperature", PropertyChangeType.Property, 160F);
        }
    }
}
