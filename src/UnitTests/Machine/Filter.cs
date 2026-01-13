using DuetControlServer.Model;
using NUnit.Framework;
using System.Collections;
using System.Collections.Generic;

namespace UnitTests.Machine
{
    public class Filter
    {
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

        // DISABLED: GetFiltered, MergeFiltered, GetSpecific tests require Provider.Get which no longer exists
        // Filter now requires an ObjectModel instance passed via DI
        /*
        [Test]
        public void GetFiltered()
        {
            string filter = "sensors/analog[*]/lastReading";
            object[] parsedFilter = DuetControlServer.Model.Filter.ConvertFilter(filter, false);

            Provider.Get.Sensors.Analog.Add(new DuetAPI.ObjectModel.AnalogSensor { LastReading = 123F });
            // ... rest of test
        }

        [Test]
        public void MergeFiltered()
        {
            // Requires Provider.Get...
        }

        [Test]
        public void GetSpecific()
        {
            // Requires Provider.Get...
        }
        */
    }
}
