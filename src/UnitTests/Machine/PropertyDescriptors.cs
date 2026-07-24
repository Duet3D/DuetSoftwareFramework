using DuetAPI.ObjectModel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using DuetControlServer;
using DcsModel = DuetControlServer.Model.ObjectModel;

namespace UnitTests.Machine
{
    /// <summary>
    /// Tests for the generated object model type descriptors and property accessors that let the object model be
    /// walked and read without reflection
    /// </summary>
    [TestFixture]
    public class PropertyDescriptors
    {
        private sealed class TestLifetime : IHostApplicationLifetime
        {
            public CancellationToken ApplicationStarted => CancellationToken.None;
            public CancellationToken ApplicationStopping => CancellationToken.None;
            public CancellationToken ApplicationStopped => CancellationToken.None;
            public void StopApplication() { }
        }

        /// <summary>
        /// Walk the whole reachable descriptor graph starting at the root object model
        /// </summary>
        private static IEnumerable<IModelObjectDescriptor> AllDescriptors()
        {
            HashSet<IModelObjectDescriptor> visited = [];
            Queue<IModelObjectDescriptor> pending = new();
            pending.Enqueue(ObjectModel.TypeDescriptor);

            while (pending.TryDequeue(out IModelObjectDescriptor descriptor))
            {
                if (visited.Add(descriptor))
                {
                    yield return descriptor;
                    foreach (ModelPropertyDescriptor property in descriptor.Properties)
                    {
                        if (property.ElementDescriptor is not null)
                        {
                            pending.Enqueue(property.ElementDescriptor);
                        }
                    }
                }
            }
        }

        private static void AssertPropertyValuesMatchClrProperties(IModelObjectAccessor accessor)
        {
            Type type = accessor.GetType();
            foreach (ModelPropertyDescriptor property in accessor.Descriptor.Properties)
            {
                PropertyInfo clrProperty = type.GetProperty(property.Name, BindingFlags.Public | BindingFlags.Instance);
                Assert.That(clrProperty, Is.Not.Null, $"{type.Name} has no CLR property named {property.Name}");

                object expected = clrProperty.GetValue(accessor), actual = accessor.GetPropertyValue(property.Index);
                if (expected is null || expected.GetType().IsValueType)
                {
                    Assert.That(actual, Is.EqualTo(expected), $"{type.Name}.{property.Name}");
                }
                else
                {
                    Assert.That(actual, Is.SameAs(expected), $"{type.Name}.{property.Name}");
                }
            }
        }

        [Test]
        public void ObjectModelPropertyValues()
        {
            ObjectModel model = new();
            model.Heat.ColdExtrudeTemperature = 145F;
            model.Job.FilePosition = 12345678;
            model.Network.Hostname = "duet3";
            AssertPropertyValuesMatchClrProperties(model);
        }

        [Test]
        public void HeatPropertyValues()
        {
            Heat heat = new();
            heat.BedHeaterMapping.Add([0]);
            heat.Heaters.Add(new Heater { Current = 25.01F });
            heat.ColdRetractTemperature = 90F;
            AssertPropertyValuesMatchClrProperties(heat);
        }

        [Test]
        public void JobPropertyValues()
        {
            Job job = new()
            {
                Duration = 42,
                LastFileName = "0:/gcodes/test.g",
                LastFileAborted = true
            };
            job.Layers.Add(new Layer { Duration = 12.5F });
            AssertPropertyValuesMatchClrProperties(job);
        }

        [Test]
        public void MovePropertyValues()
        {
            Move move = new();
            move.Axes.Add(new Axis { Letter = 'X', MachinePosition = 42.5F });
            move.SpeedFactor = 0.5F;
            AssertPropertyValuesMatchClrProperties(move);
        }

        [Test]
        public void ToolAndInputChannelPropertyValues()
        {
            Tool tool = new() { Number = 1, Name = "extruder" };
            tool.Heaters.Add(1);
            AssertPropertyValuesMatchClrProperties(tool);

            InputChannel channel = new() { Name = DuetAPI.CodeChannel.File, LineNumber = 123 };
            AssertPropertyValuesMatchClrProperties(channel);
        }

        [Test]
        public void PropertyIndicesMatchPosition()
        {
            foreach (IModelObjectDescriptor descriptor in AllDescriptors())
            {
                for (int i = 0; i < descriptor.Properties.Count; i++)
                {
                    Assert.That(descriptor.Properties[i].Index, Is.EqualTo(i));
                }
            }
        }

        [Test]
        public void JsonNamesAreCamelCased()
        {
            foreach (IModelObjectDescriptor descriptor in AllDescriptors())
            {
                foreach (ModelPropertyDescriptor property in descriptor.Properties)
                {
                    Assert.That(property.JsonName, Is.EqualTo(JsonNamingPolicy.CamelCase.ConvertName(property.Name)));
                }
            }
        }

        [Test]
        public void DescriptorGraphIsNonTrivial()
        {
            // Guards against the walk above silently degenerating into a single descriptor
            Assert.That(ObjectModel.TypeDescriptor.Properties.Count, Is.GreaterThan(10));
            Assert.That(new List<IModelObjectDescriptor>(AllDescriptors()).Count, Is.GreaterThan(50));
        }

        [Test]
        public void FindPropertyIsCaseSensitiveByDefault()
        {
            ModelPropertyDescriptor property = ObjectModel.TypeDescriptor.FindProperty("Heat", false);
            Assert.That(property, Is.Not.Null);
            Assert.That(property.Name, Is.EqualTo("Heat"));
            Assert.That(property.JsonName, Is.EqualTo("heat"));

            Assert.That(ObjectModel.TypeDescriptor.FindProperty("heat", false), Is.Null);
            Assert.That(ObjectModel.TypeDescriptor.FindProperty("nonExistingProperty", false), Is.Null);
        }

        [Test]
        public void FindPropertyIgnoringCase()
        {
            ModelPropertyDescriptor property = ObjectModel.TypeDescriptor.FindProperty("heat", true);
            Assert.That(property, Is.Not.Null);
            Assert.That(property, Is.SameAs(ObjectModel.TypeDescriptor.FindProperty("Heat", false)));
            Assert.That(ObjectModel.TypeDescriptor.FindProperty("HEAT", true), Is.SameAs(property));

            Assert.That(ObjectModel.TypeDescriptor.FindProperty("nonExistingProperty", true), Is.Null);
        }

        [Test]
        public void FindPropertyByJsonName()
        {
            ModelPropertyDescriptor property = ObjectModel.TypeDescriptor.FindPropertyByJsonName("ledStrips");
            Assert.That(property, Is.Not.Null);
            Assert.That(property.Name, Is.EqualTo("LedStrips"));

            // The CLR name is not a valid JSON name
            Assert.That(ObjectModel.TypeDescriptor.FindPropertyByJsonName("LedStrips"), Is.Null);
            Assert.That(ObjectModel.TypeDescriptor.FindPropertyByJsonName("nonExistingProperty"), Is.Null);
        }

        [Test]
        public void PropertyFlags()
        {
            Assert.That(ObjectModel.TypeDescriptor.FindProperty("Messages", false).Flags, Is.EqualTo(ModelPropertyFlags.SbcProperty));
            Assert.That(ObjectModel.TypeDescriptor.FindProperty("Limits", false).Flags, Is.EqualTo(ModelPropertyFlags.Verbose));
            Assert.That(ObjectModel.TypeDescriptor.FindProperty("SBC", false).Flags, Is.EqualTo(ModelPropertyFlags.HasSetter | ModelPropertyFlags.SbcProperty));
            Assert.That(Heat.TypeDescriptor.FindProperty("Heaters", false).Flags, Is.EqualTo(ModelPropertyFlags.Live));
            Assert.That(Heat.TypeDescriptor.FindProperty("BedHeaters", false).Flags, Is.EqualTo(ModelPropertyFlags.Obsolete));
            Assert.That(Heat.TypeDescriptor.FindProperty("ColdExtrudeTemperature", false).Flags, Is.EqualTo(ModelPropertyFlags.HasSetter));
            Assert.That(Network.TypeDescriptor.FindProperty("CorsSite", false).Flags, Is.EqualTo(ModelPropertyFlags.HasSetter | ModelPropertyFlags.SbcProperty));

            // Read-only collections and dictionaries have no setter
            Assert.That(ObjectModel.TypeDescriptor.FindProperty("Tools", false).Flags & ModelPropertyFlags.HasSetter, Is.EqualTo(ModelPropertyFlags.None));
            Assert.That(ObjectModel.TypeDescriptor.FindProperty("Global", false).Flags & ModelPropertyFlags.HasSetter, Is.EqualTo(ModelPropertyFlags.None));
        }

        [Test]
        public void PropertyKinds()
        {
            Assert.That(ObjectModel.TypeDescriptor.FindProperty("Heat", false).Kind, Is.EqualTo(ModelPropertyKind.ModelObject));
            Assert.That(ObjectModel.TypeDescriptor.FindProperty("Tools", false).Kind, Is.EqualTo(ModelPropertyKind.ModelCollection));
            Assert.That(ObjectModel.TypeDescriptor.FindProperty("Global", false).Kind, Is.EqualTo(ModelPropertyKind.ModelDictionary));
            Assert.That(ObjectModel.TypeDescriptor.FindProperty("Plugins", false).Kind, Is.EqualTo(ModelPropertyKind.ModelDictionary));
            Assert.That(Heat.TypeDescriptor.FindProperty("BedHeaters", false).Kind, Is.EqualTo(ModelPropertyKind.ObservableCollection));
            Assert.That(Heat.TypeDescriptor.FindProperty("ColdExtrudeTemperature", false).Kind, Is.EqualTo(ModelPropertyKind.Value));
            Assert.That(Job.TypeDescriptor.FindProperty("LastFileName", false).Kind, Is.EqualTo(ModelPropertyKind.Value));
        }

        [Test]
        public void ElementDescriptors()
        {
            Assert.That(ObjectModel.TypeDescriptor.FindProperty("Heat", false).ElementDescriptor, Is.SameAs(Heat.TypeDescriptor));
            Assert.That(ObjectModel.TypeDescriptor.FindProperty("Tools", false).ElementDescriptor, Is.SameAs(Tool.TypeDescriptor));
            Assert.That(ObjectModel.TypeDescriptor.FindProperty("Move", false).ElementDescriptor, Is.SameAs(Move.TypeDescriptor));

            // Inputs derives from StaticModelCollection<InputChannel> instead of using it directly, so the item type
            // has to be resolved through the base class as well
            Assert.That(ObjectModel.TypeDescriptor.FindProperty("Inputs", false).ElementDescriptor, Is.SameAs(InputChannel.TypeDescriptor));

            // Scalars have no element type and Message is not a model object
            Assert.That(Heat.TypeDescriptor.FindProperty("ColdExtrudeTemperature", false).ElementDescriptor, Is.Null);
            Assert.That(ObjectModel.TypeDescriptor.FindProperty("Messages", false).ElementDescriptor, Is.Null);
        }

        [Test]
        public void GetPropertyValueRejectsInvalidIndices()
        {
            ObjectModel model = new();
            Assert.Throws<ArgumentOutOfRangeException>(() => model.GetPropertyValue(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => model.GetPropertyValue(ObjectModel.TypeDescriptor.Properties.Count));

            Heat heat = new();
            Assert.Throws<ArgumentOutOfRangeException>(() => heat.GetPropertyValue(Heat.TypeDescriptor.Properties.Count));
        }

        [Test]
        public void DcsObjectModelDoesNotExposeExtraProperties()
        {
            IOptions<Settings> settings = Options.Create(new Settings());
            DcsModel model = new(new TestLifetime(), NullLogger<DcsModel>.Instance, settings);

            // DCS adds its own members to the object model, but the descriptor must only describe the API model
            Assert.That(model.Descriptor.Properties.Count, Is.EqualTo(ObjectModel.TypeDescriptor.Properties.Count));
            Assert.That(model.Descriptor, Is.SameAs(ObjectModel.TypeDescriptor));
        }
    }
}
