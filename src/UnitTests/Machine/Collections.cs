using DuetAPI.ObjectModel;
using NUnit.Framework;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace UnitTests.Machine
{
    [TestFixture]
    public class Collections
    {
        [Test]
        public void UpdateCollectionFromJsonReaderWithNullItems()
        {
            ObjectModel model = new();

            // Populate three fans
            Utf8JsonReader reader = new(Encoding.UTF8.GetBytes("[{\"actualValue\":0.5},{\"actualValue\":0.7},{\"actualValue\":0.9}]"));
            reader.Read();
            model.Fans.UpdateFromJsonReader(ref reader, false);

            Assert.That(model.Fans, Has.Count.EqualTo(3));
            Assert.That(model.Fans[1], Is.Not.Null);

            // A null item must overwrite the existing fan instead of appending a new element
            reader = new(Encoding.UTF8.GetBytes("[{\"actualValue\":0.5},null,{\"actualValue\":0.9}]"));
            reader.Read();
            model.Fans.UpdateFromJsonReader(ref reader, false);

            Assert.That(model.Fans, Has.Count.EqualTo(3));
            Assert.That(model.Fans[0], Is.Not.Null);
            Assert.That(model.Fans[1], Is.Null);
            Assert.That(model.Fans[2], Is.Not.Null);
            Assert.That(model.Fans[2].ActualValue, Is.EqualTo(0.9F));
        }

        [Test]
        public void StaticModelDictionaryCloneIsDeep()
        {
            StaticModelDictionary<Plugin> plugins = new(true, true);
            Plugin plugin = new() { Id = "test", Name = "test", Pid = 1234 };
            plugins.Add("test", plugin);

            StaticModelDictionary<Plugin> clone = (StaticModelDictionary<Plugin>)plugins.Clone();
            Assert.That(ReferenceEquals(clone["test"], plugin), Is.False);

            // Mutating the original must not affect the clone
            plugin.Pid = 5678;
            Assert.That(clone["test"].Pid, Is.EqualTo(1234));
        }

        [Test]
        public void StaticModelDictionaryAssignIsDeep()
        {
            StaticModelDictionary<Plugin> source = new(true, true);
            Plugin plugin = new() { Id = "test", Name = "test", Pid = 1234 };
            source.Add("test", plugin);

            StaticModelDictionary<Plugin> target = new(true, true);
            target.Assign(source);

            Assert.That(target.ContainsKey("test"), Is.True);
            Assert.That(ReferenceEquals(target["test"], plugin), Is.False);
            Assert.That(target["test"].Pid, Is.EqualTo(1234));
        }

        [Test]
        public void StaticModelDictionaryCopyTo()
        {
            StaticModelDictionary<Plugin> plugins = new(true, true);
            plugins.Add("test", new Plugin { Id = "test", Name = "test", Pid = 1234 });

            // ToList goes through ICollection<T>.CopyTo
            List<KeyValuePair<string, Plugin>> items = plugins.ToList();
            Assert.That(items, Has.Count.EqualTo(1));
            Assert.That(items[0].Key, Is.EqualTo("test"));
            Assert.That(items[0].Value.Pid, Is.EqualTo(1234));
        }

        [Test]
        public void CloneModelWithPlugins()
        {
            ObjectModel model = new();
            model.Plugins.Add("test", new Plugin { Id = "test", Name = "test", Pid = 1234 });

            // Cloning the whole model must produce deep plugin copies
            ObjectModel clone = (ObjectModel)model.Clone();
            Assert.That(clone.Plugins.ContainsKey("test"), Is.True);
            Assert.That(ReferenceEquals(clone.Plugins["test"], model.Plugins["test"]), Is.False);

            // Default instances must remain cloneable, so the validating setters must accept their default state
            Assert.DoesNotThrow(() => new Plugin().Clone());
        }

        [Test]
        public void JsonModelDictionaryChangeDetection()
        {
            ObjectModel model = new();

            int numChangedEvents = 0;
            model.Global.PropertyChanged += (_, _) => numChangedEvents++;

            // First update must fire a change event
            using (JsonDocument json = JsonDocument.Parse("{\"myVar\":123}"))
            {
                model.Global.UpdateFromJson(json.RootElement, false);
            }
            Assert.That(numChangedEvents, Is.EqualTo(1));
            Assert.That(model.Global["myVar"].Value.GetInt32(), Is.EqualTo(123));

            // The same value coming from a different JSON document must not fire another one
            using (JsonDocument json = JsonDocument.Parse("{\"myVar\":123}"))
            {
                model.Global.UpdateFromJson(json.RootElement, false);
            }
            Assert.That(numChangedEvents, Is.EqualTo(1));

            // A different value must fire one again
            using (JsonDocument json = JsonDocument.Parse("{\"myVar\":456}"))
            {
                model.Global.UpdateFromJson(json.RootElement, false);
            }
            Assert.That(numChangedEvents, Is.EqualTo(2));
            Assert.That(model.Global["myVar"].Value.GetInt32(), Is.EqualTo(456));
        }

        [Test]
        public void KinematicsMatrixChangeDetection()
        {
            CoreKinematics kinematics = new();

            int numChangedEvents = 0;
            kinematics.ForwardMatrix.CollectionChanged += (_, _) => numChangedEvents++;

            // Updating with the default identity matrix must not raise Replace events
            using (JsonDocument json = JsonDocument.Parse("{\"forwardMatrix\":[[1,0,0],[0,1,0],[0,0,1]]}"))
            {
                kinematics.UpdateFromJson(json.RootElement, false);
            }
            Assert.That(numChangedEvents, Is.EqualTo(0));

            // Same for the reader-based update path
            Utf8JsonReader reader = new(Encoding.UTF8.GetBytes("{\"forwardMatrix\":[[1,0,0],[0,1,0],[0,0,1]]}"));
            reader.Read();
            kinematics.UpdateFromJsonReader(ref reader, false);
            Assert.That(numChangedEvents, Is.EqualTo(0));

            // A different matrix must raise an event
            using (JsonDocument json = JsonDocument.Parse("{\"forwardMatrix\":[[1,0,0],[0,1,0],[0.5,0,1]]}"))
            {
                kinematics.UpdateFromJson(json.RootElement, false);
            }
            Assert.That(numChangedEvents, Is.EqualTo(1));
        }
    }
}
