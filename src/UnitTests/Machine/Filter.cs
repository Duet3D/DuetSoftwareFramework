using DuetControlServer;
using DuetControlServer.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ApiModel = DuetAPI.ObjectModel.ObjectModel;
using DcsFilter = DuetControlServer.Model.Filter;
using DcsModel = DuetControlServer.Model.ObjectModel;

namespace UnitTests.Machine
{
    public class Filter
    {
        private sealed class TestLifetime : IHostApplicationLifetime
        {
            public CancellationToken ApplicationStarted => CancellationToken.None;
            public CancellationToken ApplicationStopping => CancellationToken.None;
            public CancellationToken ApplicationStopped => CancellationToken.None;
            public void StopApplication() { }
        }

        private static DcsModel CreateModel() => new(new TestLifetime(), NullLogger<DcsModel>.Instance, Options.Create(new Settings()));

        private static Dictionary<string, object> SubDictionary(Dictionary<string, object> parent, string key)
        {
            Assert.That(parent.ContainsKey(key), Is.True, $"missing key {key}");
            Assert.That(parent[key], Is.InstanceOf<Dictionary<string, object>>(), $"{key} is not a sub-object");
            return (Dictionary<string, object>)parent[key];
        }

        [Test]
        public void ParseFilters()
        {
            string filters = "heat/heaters[*]/active|state/status|directories/web";
            object[][] parsedFilters = DuetControlServer.Model.Filter.ConvertFilters(filters);

            Assert.That(parsedFilters, Has.Length.EqualTo(3));

            Assert.That(parsedFilters[0][0], Is.EqualTo("heat"));
            Assert.That(parsedFilters[0][1], Is.EqualTo("heaters"));
            Assert.That(parsedFilters[0][2], Is.EqualTo(-1));
            Assert.That(parsedFilters[0][3], Is.EqualTo("active"));

            Assert.That(parsedFilters[1][0], Is.EqualTo("state"));
            Assert.That(parsedFilters[1][1], Is.EqualTo("status"));

            Assert.That(parsedFilters[2][0], Is.EqualTo("directories"));
            Assert.That(parsedFilters[2][1], Is.EqualTo("web"));
        }

        [Test]
        public void ParseSingleFilter()
        {
            string filters = "heat/heaters[*]/active";
            object[][] parsedFilters = DuetControlServer.Model.Filter.ConvertFilters(filters);

            Assert.That(parsedFilters, Has.Length.EqualTo(1));

            Assert.That(parsedFilters[0][0], Is.EqualTo("heat"));
            Assert.That(parsedFilters[0][1], Is.EqualTo("heaters"));
            Assert.That(parsedFilters[0][2], Is.EqualTo(-1));
            Assert.That(parsedFilters[0][3], Is.EqualTo("active"));
        }

        [Test]
        public void CheckFilters()
        {
            object[] pathA = ["sensors", new ItemPathNode("analog", 0, new object[3])];
            object[] filterA = ["sensors", "analog", -1, "lastReading"];
            Assert.That(DuetControlServer.Model.Filter.PathMatches(pathA, filterA), Is.True);

            object[] pathB = ["state", "currentTool"];
            object[] filterB = ["state", "currentTool"];
            Assert.That(DuetControlServer.Model.Filter.PathMatches(pathB, filterB), Is.True);

            object[] pathC = ["state", "status"];
            object[] filterC = ["state", "**"];
            Assert.That(DuetControlServer.Model.Filter.PathMatches(pathC, filterC), Is.True);

            object[] pathD = ["state", "status"];
            object[] filterD = ["state"];
            Assert.That(DuetControlServer.Model.Filter.PathMatches(pathD, filterD), Is.False);
        }

        [Test]
        public void CheckMultipleFilters()
        {
            object[][] filters = DuetControlServer.Model.Filter.ConvertFilters("directories/www|httpEndpoints/**|userSessions/**");
            object[] otherPath = [new ItemPathNode("boards", 0, new object[1]), "mcuTemp", "current"];
            foreach (object[] filter in filters)
            {
                bool pathMatches = DuetControlServer.Model.Filter.PathMatches(otherPath, filter);
                Assert.That(pathMatches, Is.False);
            }
        }

        [Test]
        public void GetFilteredExactPath()
        {
            DcsModel model = CreateModel();
            model.Heat.ColdExtrudeTemperature = 145F;

            Dictionary<string, object> result = new DcsFilter(model).GetFiltered("heat/coldExtrudeTemperature");
            Assert.That(result.Keys, Is.EquivalentTo(new[] { "heat" }));

            Dictionary<string, object> heat = SubDictionary(result, "heat");
            Assert.That(heat.Keys, Is.EquivalentTo(new[] { "coldExtrudeTemperature" }));
            Assert.That(heat["coldExtrudeTemperature"], Is.EqualTo(145F));
        }

        [Test]
        public void GetFilteredWildcards()
        {
            DcsModel model = CreateModel();
            model.Heat.ColdExtrudeTemperature = 145F;
            DcsFilter filter = new(model);

            // A single wildcard returns every property of that level
            Dictionary<string, object> root = filter.GetFiltered("*");
            Assert.That(root.Keys, Is.EquivalentTo(ApiModel.TypeDescriptor.Properties.Select(property => property.JsonName)));

            Dictionary<string, object> heat = SubDictionary(filter.GetFiltered("heat/*"), "heat");
            Assert.That(heat.Keys, Is.EquivalentTo(DuetAPI.ObjectModel.Heat.TypeDescriptor.Properties.Select(property => property.JsonName)));

            // Sub-objects are only expanded recursively if query flags are given
            Dictionary<string, object> recursed = filter.GetFiltered("**", QueryFlags.Parse(null));
            Assert.That(SubDictionary(recursed, "heat")["coldExtrudeTemperature"], Is.EqualTo(145F));
            Assert.That(SubDictionary(SubDictionary(recursed, "move"), "currentMove"), Is.Not.Empty);
        }

        [Test]
        public void GetFilteredKeysAreCamelCased()
        {
            DcsModel model = CreateModel();
            Dictionary<string, object> state = SubDictionary(new DcsFilter(model).GetFiltered("state/*"), "state");

            Assert.That(state.ContainsKey("atxPower"), Is.True);
            Assert.That(state.ContainsKey("displayMessage"), Is.True);
            Assert.That(state.ContainsKey("msUpTime"), Is.True);
            Assert.That(state.ContainsKey("AtxPower"), Is.False);
        }

        [Test]
        public void GetFilteredLiveOnly()
        {
            DcsModel model = CreateModel();
            Dictionary<string, object> result = new DcsFilter(model).GetFiltered("*", QueryFlags.Parse("f"));

            IEnumerable<string> liveProperties = ApiModel.TypeDescriptor.Properties
                .Where(property => (property.Flags & DuetAPI.ObjectModel.ModelPropertyFlags.Live) != 0)
                .Select(property => property.JsonName);
            Assert.That(liveProperties, Is.Not.Empty);
            Assert.That(result.Keys, Is.EquivalentTo(liveProperties));
            Assert.That(result.ContainsKey("network"), Is.False);
        }

        [Test]
        public void GetFilteredVerboseAndObsolete()
        {
            DcsModel model = CreateModel();
            DcsFilter filter = new(model);

            Assert.That(filter.GetFiltered("*", QueryFlags.Parse(null)).ContainsKey("limits"), Is.False);
            Assert.That(filter.GetFiltered("*", QueryFlags.Parse("v")).ContainsKey("limits"), Is.True);

            Assert.That(SubDictionary(filter.GetFiltered("heat/*", QueryFlags.Parse(null)), "heat").ContainsKey("bedHeaters"), Is.False);
            Assert.That(SubDictionary(filter.GetFiltered("heat/*", QueryFlags.Parse("o")), "heat").ContainsKey("bedHeaters"), Is.True);
        }

        [Test]
        public void GetFilteredNulls()
        {
            DcsModel model = CreateModel();
            DcsFilter filter = new(model);
            Assert.That(model.State.AtxPower, Is.Null);

            Assert.That(SubDictionary(filter.GetFiltered("**", QueryFlags.Parse(null)), "state").ContainsKey("atxPower"), Is.False);

            Dictionary<string, object> state = SubDictionary(filter.GetFiltered("**", QueryFlags.Parse("n")), "state");
            Assert.That(state.ContainsKey("atxPower"), Is.True);
            Assert.That(state["atxPower"], Is.Null);
        }

        [Test]
        public void GetFilteredMaxDepth()
        {
            DcsModel model = CreateModel();
            Dictionary<string, object> result = new DcsFilter(model).GetFiltered("**", QueryFlags.Parse("d1"));

            Assert.That(result.ContainsKey("state"), Is.True);
            Assert.That(SubDictionary(result, "state"), Is.Empty);
            Assert.That(SubDictionary(result, "heat"), Is.Empty);
        }

        [Test]
        public void GetSpecific()
        {
            DcsModel model = CreateModel();
            model.Heat.ColdExtrudeTemperature = 145F;
            model.Network.Hostname = "duet3";
            DcsFilter filter = new(model);

            Assert.That(filter.GetSpecific("heat/coldExtrudeTemperature", out object result), Is.True);
            Assert.That(result, Is.EqualTo(145F));

            Assert.That(filter.GetSpecific("network/hostname", out result), Is.True);
            Assert.That(result, Is.EqualTo("duet3"));

            // Property names are matched against the CLR names without regard to case
            Assert.That(filter.GetSpecific("HEAT/COLDEXTRUDETEMPERATURE", out result), Is.True);
            Assert.That(result, Is.EqualTo(145F));

            Assert.That(filter.GetSpecific("heat/nonExistingProperty", out result), Is.False);
            Assert.That(result, Is.Null);

            Assert.That(filter.GetSpecific("nonExistingProperty", out result), Is.False);
            Assert.That(result, Is.Null);
        }

        [Test]
        public void GetSpecificFormerSbcProperties()
        {
            // The SBC-property distinction is gone: these resolve like any other field
            DcsModel model = CreateModel();
            model.Network.CorsSite = "http://localhost";
            model.State.LogFile = "0:/sys/eventlog.txt";
            DcsFilter filter = new(model);

            Assert.That(filter.GetSpecific("network/corsSite", out object result), Is.True);
            Assert.That(result, Is.EqualTo("http://localhost"));

            Assert.That(filter.GetSpecific("state/logFile", out result), Is.True);
            Assert.That(result, Is.EqualTo("0:/sys/eventlog.txt"));
        }
    }
}
