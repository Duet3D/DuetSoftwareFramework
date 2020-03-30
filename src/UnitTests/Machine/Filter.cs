using DuetControlServer.Model;
using NUnit.Framework;
using System.Collections;
using System.Collections.Generic;

namespace UnitTests.Machine
{
    [TestFixture]
    public class Filter
    {
        [Test]
        public void ParseFilters()
        {
            string filters = "heat/heaters[*]/active|state/status|directories/web";
            object[][] parsedFilters = DuetControlServer.Model.Filter.ConvertFilters(filters);

            Assert.AreEqual(3, parsedFilters.Length);

            Assert.AreEqual("heat", parsedFilters[0][0]);
            Assert.AreEqual("heaters", parsedFilters[0][1]);
            Assert.AreEqual(-1, parsedFilters[0][2]);
            Assert.AreEqual("active", parsedFilters[0][3]);

            Assert.AreEqual("state", parsedFilters[1][0]);
            Assert.AreEqual("status", parsedFilters[1][1]);

            Assert.AreEqual("directories", parsedFilters[2][0]);
            Assert.AreEqual("web", parsedFilters[2][1]);
        }

        [Test]
        public void CheckFilters()
        {
            object[] pathA = new object[] { "sensors", new ItemPathNode("analog", 0, new object[3]) };
            object[] filterA = new object[] { "sensors", "analog", -1, "lastReading" };
            Assert.IsTrue(DuetControlServer.Model.Filter.PathMatches(pathA, filterA));

            object[] pathB = new object[] { "state", "currentTool" };
            object[] filterB = new object[] { "state", "currentTool" };
            Assert.IsTrue(DuetControlServer.Model.Filter.PathMatches(pathB, filterB));

            object[] pathC = new object[] { "state", "status" };
            object[] filterC = new object[] { "state", "**" };
            Assert.IsTrue(DuetControlServer.Model.Filter.PathMatches(pathC, filterC));

            object[] pathD = new object[] { "state", "status" };
            object[] filterD = new object[] { "state" };
            Assert.IsFalse(DuetControlServer.Model.Filter.PathMatches(pathD, filterD));
        }

        [Test]
        public void CheckMultipleFilters()
        {
            object[][] filters = DuetControlServer.Model.Filter.ConvertFilters("directories/www|httpEndpoints/**|userSessions/**");
            object[] otherPath = new object[] { new ItemPathNode("boards", 0, new object[1]), "mcuTemp", "current" };
            foreach (object[] filter in filters)
            {
                bool pathMatches = DuetControlServer.Model.Filter.PathMatches(otherPath, filter);
                Assert.IsFalse(pathMatches);
            }
        }

        [Test]
        public void GetFiltered()
        {
            string filter = "sensors/analog[*]/lastReading";
            object[] parsedFilter = DuetControlServer.Model.Filter.ConvertFilter(filter, false);

            Provider.Get.Sensors.Analog.Add(new DuetAPI.Machine.AnalogSensor { LastReading = 123F });
            Provider.Get.Sensors.Analog.Add(null);
            Provider.Get.Sensors.Analog.Add(new DuetAPI.Machine.AnalogSensor { LastReading = 456F });

            Dictionary<string, object> partialModel = DuetControlServer.Model.Filter.GetFiltered(parsedFilter);

            Dictionary<string, object> sensorsKey = (Dictionary<string, object>)partialModel["sensors"];
            Assert.AreEqual(1, sensorsKey.Count);
            List<object> analogKey = (List<object>)sensorsKey["analog"];
            Assert.AreEqual(3, analogKey.Count);
            Dictionary<string, object> firstSensor = (Dictionary<string, object>)analogKey[0];
            Assert.AreEqual(1, firstSensor.Count);
            Assert.AreEqual(123F, firstSensor["lastReading"]);
            Dictionary<string, object> secondSensor = (Dictionary<string, object>)analogKey[1];
            Assert.IsNull(secondSensor);
            Dictionary<string, object> thirdSensor = (Dictionary<string, object>)analogKey[2];
            Assert.AreEqual(1, thirdSensor.Count);
            Assert.AreEqual(456F, thirdSensor["lastReading"]);
        }

        [Test]
        public void MergeFiltered()
        {
            string filterA = "tools[*]/active";
            object[] parsedFilterA = DuetControlServer.Model.Filter.ConvertFilter(filterA, false);
            string filterB = "tools[*]/standby";
            object[] parsedFilterB = DuetControlServer.Model.Filter.ConvertFilter(filterB, false);
            string filterC = "tools[*]";
            object[] parsedFilterC = DuetControlServer.Model.Filter.ConvertFilter(filterC, false);

            DuetAPI.Machine.Tool toolA = new DuetAPI.Machine.Tool();
            toolA.Active.Add(123F);
            toolA.Standby.Add(456F);
            toolA.State = DuetAPI.Machine.ToolState.Active;
            Provider.Get.Tools.Add(toolA);

            DuetAPI.Machine.Tool toolB = new DuetAPI.Machine.Tool();
            toolB.Active.Add(10F);
            toolB.Standby.Add(20F);
            toolB.State = DuetAPI.Machine.ToolState.Standby;
            Provider.Get.Tools.Add(toolB);

            // Query filter A
            Dictionary<string, object> partialModelA = DuetControlServer.Model.Filter.GetFiltered(parsedFilterA);
            List<object> toolsKeyA = (List<object>)partialModelA["tools"];
            Dictionary<string, object> toolOneA = (Dictionary<string, object>)toolsKeyA[0];
            Assert.AreEqual(1, toolOneA.Count);
            Assert.AreEqual(new List<object> { 123F }, toolOneA["active"]);
            Dictionary<string, object> toolTwoA = (Dictionary<string, object>)toolsKeyA[1];
            Assert.AreEqual(1, toolTwoA.Count);
            Assert.AreEqual(new List<object> { 10F }, toolTwoA["active"]);

            // Query filter B
            Dictionary<string, object> partialModelB = DuetControlServer.Model.Filter.GetFiltered(parsedFilterB);
            List<object> toolsKeyB = (List<object>)partialModelB["tools"];
            Dictionary<string, object> toolOneB = (Dictionary<string, object>)toolsKeyB[0];
            Assert.AreEqual(1, toolOneB.Count);
            Assert.AreEqual(new List<object> { 456F }, toolOneB["standby"]);
            Dictionary<string, object> toolTwoB = (Dictionary<string, object>)toolsKeyB[1];
            Assert.AreEqual(1, toolTwoB.Count);
            Assert.AreEqual(new List<object> { 20F }, toolTwoB["standby"]);

            // Query filter C
            Dictionary<string, object> partialModelC = DuetControlServer.Model.Filter.GetFiltered(parsedFilterC);
            IList toolsKeyC = (IList)partialModelC["tools"];
            Assert.AreEqual(2, toolsKeyC.Count);
            Assert.IsTrue(toolsKeyC[0] is DuetAPI.Machine.Tool);
            Assert.IsTrue(toolsKeyC[1] is DuetAPI.Machine.Tool);

            // Merge A+B
            Dictionary<string, object> merged = new Dictionary<string, object>();
            DuetControlServer.Model.Filter.MergeFiltered(merged, partialModelA);
            DuetControlServer.Model.Filter.MergeFiltered(merged, partialModelB);
            List<object> mergedTools = (List<object>)merged["tools"];
            Dictionary<string, object> mergedToolA = (Dictionary<string, object>)mergedTools[0];
            Assert.AreEqual(2, mergedToolA.Count);
            Assert.AreEqual(new List<object> { 123F }, mergedToolA["active"]);
            Assert.AreEqual(new List<object> { 456F }, mergedToolA["standby"]);
            Dictionary<string, object> mergedToolB = (Dictionary<string, object>)mergedTools[1];
            Assert.AreEqual(2, mergedToolB.Count);
            Assert.AreEqual(new List<object> { 10F }, mergedToolB["active"]);
            Assert.AreEqual(new List<object> { 20F }, mergedToolB["standby"]);

            // Merge A+C
            DuetControlServer.Model.Filter.MergeFiltered(merged, partialModelC);
            mergedTools = (List<object>)merged["tools"];
            Assert.IsTrue(mergedTools[0] is DuetAPI.Machine.Tool);
            Assert.IsTrue(mergedTools[1] is DuetAPI.Machine.Tool);
        }

        [Test]
        public void GetSpecific()
        {
            Provider.Get.State.Status = DuetAPI.Machine.MachineStatus.Processing;
            Assert.IsTrue(DuetControlServer.Model.Filter.GetSpecific("state.status", false, out object status));
            Assert.AreEqual(DuetAPI.Machine.MachineStatus.Processing, status);

            Provider.Get.Fans.Add(new DuetAPI.Machine.Fan() { ActualValue = 0.53F });
            Assert.IsTrue(DuetControlServer.Model.Filter.GetSpecific("fans[0].actualValue", false, out object actualValue));
            Assert.AreEqual(0.53F, actualValue);
        }
    }
}
