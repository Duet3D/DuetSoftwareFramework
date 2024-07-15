using DuetControlServer.Model;
using NUnit.Framework;
using NUnit.Framework.Legacy;
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

            ClassicAssert.AreEqual(3, parsedFilters.Length);

            ClassicAssert.AreEqual("heat", parsedFilters[0][0]);
            ClassicAssert.AreEqual("heaters", parsedFilters[0][1]);
            ClassicAssert.AreEqual(-1, parsedFilters[0][2]);
            ClassicAssert.AreEqual("active", parsedFilters[0][3]);

            ClassicAssert.AreEqual("state", parsedFilters[1][0]);
            ClassicAssert.AreEqual("status", parsedFilters[1][1]);

            ClassicAssert.AreEqual("directories", parsedFilters[2][0]);
            ClassicAssert.AreEqual("web", parsedFilters[2][1]);
        }

        [Test]
        public void ParseSingleFilter()
        {
            string filters = "heat/heaters[*]/active";
            object[][] parsedFilters = DuetControlServer.Model.Filter.ConvertFilters(filters);

            ClassicAssert.AreEqual(1, parsedFilters.Length);

            ClassicAssert.AreEqual("heat", parsedFilters[0][0]);
            ClassicAssert.AreEqual("heaters", parsedFilters[0][1]);
            ClassicAssert.AreEqual(-1, parsedFilters[0][2]);
            ClassicAssert.AreEqual("active", parsedFilters[0][3]);
        }

        [Test]
        public void CheckFilters()
        {
            object[] pathA = ["sensors", new ItemPathNode("analog", 0, new object[3])];
            object[] filterA = ["sensors", "analog", -1, "lastReading"];
            ClassicAssert.IsTrue(DuetControlServer.Model.Filter.PathMatches(pathA, filterA));

            object[] pathB = ["state", "currentTool"];
            object[] filterB = ["state", "currentTool"];
            ClassicAssert.IsTrue(DuetControlServer.Model.Filter.PathMatches(pathB, filterB));

            object[] pathC = ["state", "status"];
            object[] filterC = ["state", "**"];
            ClassicAssert.IsTrue(DuetControlServer.Model.Filter.PathMatches(pathC, filterC));

            object[] pathD = ["state", "status"];
            object[] filterD = ["state"];
            ClassicAssert.IsFalse(DuetControlServer.Model.Filter.PathMatches(pathD, filterD));
        }

        [Test]
        public void CheckMultipleFilters()
        {
            object[][] filters = DuetControlServer.Model.Filter.ConvertFilters("directories/www|httpEndpoints/**|userSessions/**");
            object[] otherPath = [new ItemPathNode("boards", 0, new object[1]), "mcuTemp", "current"];
            foreach (object[] filter in filters)
            {
                bool pathMatches = DuetControlServer.Model.Filter.PathMatches(otherPath, filter);
                ClassicAssert.IsFalse(pathMatches);
            }
        }

        [Test]
        public void GetFiltered()
        {
            string filter = "sensors/analog[*]/lastReading";
            object[] parsedFilter = DuetControlServer.Model.Filter.ConvertFilter(filter, false);

            Provider.Get.Sensors.Analog.Add(new DuetAPI.ObjectModel.AnalogSensor { LastReading = 123F });
            Provider.Get.Sensors.Analog.Add(null);
            Provider.Get.Sensors.Analog.Add(new DuetAPI.ObjectModel.AnalogSensor { LastReading = 456F });

            Dictionary<string, object?> partialModel = DuetControlServer.Model.Filter.GetFiltered(parsedFilter);

            Dictionary<string, object?> sensorsKey = (Dictionary<string, object?>)partialModel["sensors"]!;
            ClassicAssert.AreEqual(1, sensorsKey.Count);
            List<object?> analogKey = (List<object?>)sensorsKey["analog"]!;
            ClassicAssert.AreEqual(3, analogKey.Count);
            Dictionary<string, object?> firstSensor = (Dictionary<string, object?>)analogKey[0]!;
            ClassicAssert.AreEqual(1, firstSensor.Count);
            ClassicAssert.AreEqual(123F, firstSensor["lastReading"]);
            Dictionary<string, object?> secondSensor = (Dictionary<string, object?>)analogKey[1]!;
            ClassicAssert.IsNull(secondSensor);
            Dictionary<string, object?> thirdSensor = (Dictionary<string, object?>)analogKey[2]!;
            ClassicAssert.AreEqual(1, thirdSensor.Count);
            ClassicAssert.AreEqual(456F, thirdSensor["lastReading"]);
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

            DuetAPI.ObjectModel.Tool toolA = new();
            toolA.Active.Add(123F);
            toolA.Standby.Add(456F);
            toolA.State = DuetAPI.ObjectModel.ToolState.Active;
            Provider.Get.Tools.Add(toolA);

            DuetAPI.ObjectModel.Tool toolB = new();
            toolB.Active.Add(10F);
            toolB.Standby.Add(20F);
            toolB.State = DuetAPI.ObjectModel.ToolState.Standby;
            Provider.Get.Tools.Add(toolB);

            // Query filter A
            Dictionary<string, object?> partialModelA = DuetControlServer.Model.Filter.GetFiltered(parsedFilterA);
            List<object?> toolsKeyA = (List<object?>)partialModelA["tools"]!;
            Dictionary<string, object?> toolOneA = (Dictionary<string, object?>)toolsKeyA[0]!;
            ClassicAssert.AreEqual(1, toolOneA.Count);
            ClassicAssert.AreEqual(new List<object?> { 123F }, toolOneA["active"]);
            Dictionary<string, object?> toolTwoA = (Dictionary<string, object?>)toolsKeyA[1]!;
            ClassicAssert.AreEqual(1, toolTwoA.Count);
            ClassicAssert.AreEqual(new List<object?> { 10F }, toolTwoA["active"]);

            // Query filter B
            Dictionary<string, object?> partialModelB = DuetControlServer.Model.Filter.GetFiltered(parsedFilterB);
            List<object?> toolsKeyB = (List<object?>)partialModelB["tools"]!;
            Dictionary<string, object?> toolOneB = (Dictionary<string, object?>)toolsKeyB[0]!;
            ClassicAssert.AreEqual(1, toolOneB.Count);
            ClassicAssert.AreEqual(new List<object> { 456F }, toolOneB["standby"]);
            Dictionary<string, object?> toolTwoB = (Dictionary<string, object?>)toolsKeyB[1]!;
            ClassicAssert.AreEqual(1, toolTwoB.Count);
            ClassicAssert.AreEqual(new List<object> { 20F }, toolTwoB["standby"]);

            // Query filter C
            Dictionary<string, object?> partialModelC = DuetControlServer.Model.Filter.GetFiltered(parsedFilterC);
            IList toolsKeyC = (IList)partialModelC["tools"]!;
            ClassicAssert.AreEqual(2, toolsKeyC.Count);
            ClassicAssert.IsTrue(toolsKeyC[0] is DuetAPI.ObjectModel.Tool);
            ClassicAssert.IsTrue(toolsKeyC[1] is DuetAPI.ObjectModel.Tool);

            // Merge A+B
            Dictionary<string, object?> merged = [];
            DuetControlServer.Model.Filter.MergeFiltered(merged, partialModelA);
            DuetControlServer.Model.Filter.MergeFiltered(merged, partialModelB);
            List<object?> mergedTools = (List<object?>)merged["tools"]!;
            Dictionary<string, object?> mergedToolA = (Dictionary<string, object?>)mergedTools[0]!;
            ClassicAssert.AreEqual(2, mergedToolA.Count);
            ClassicAssert.AreEqual(new List<object?> { 123F }, mergedToolA["active"]);
            ClassicAssert.AreEqual(new List<object?> { 456F }, mergedToolA["standby"]);
            Dictionary<string, object?> mergedToolB = (Dictionary<string, object?>)mergedTools[1]!;
            ClassicAssert.AreEqual(2, mergedToolB.Count);
            ClassicAssert.AreEqual(new List<object?> { 10F }, mergedToolB["active"]);
            ClassicAssert.AreEqual(new List<object?> { 20F }, mergedToolB["standby"]);

            // Merge A+C
            DuetControlServer.Model.Filter.MergeFiltered(merged, partialModelC);
            mergedTools = (List<object?>)merged["tools"]!;
            ClassicAssert.IsTrue(mergedTools[0] is DuetAPI.ObjectModel.Tool);
            ClassicAssert.IsTrue(mergedTools[1] is DuetAPI.ObjectModel.Tool);
        }

        [Test]
        public void GetSpecific()
        {
            Provider.Get.State.Status = DuetAPI.ObjectModel.MachineStatus.Processing;
            ClassicAssert.IsTrue(DuetControlServer.Model.Filter.GetSpecific("state.status", false, out object? status));
            ClassicAssert.AreEqual(DuetAPI.ObjectModel.MachineStatus.Processing, status);

            Provider.Get.Fans.Add(new DuetAPI.ObjectModel.Fan() { ActualValue = 0.53F });
            ClassicAssert.IsTrue(DuetControlServer.Model.Filter.GetSpecific("fans[0].actualValue", false, out object? actualValue));
            ClassicAssert.AreEqual(0.53F, actualValue);
        }
    }
}
