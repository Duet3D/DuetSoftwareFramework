using Newtonsoft.Json.Linq;
using System.Threading.Tasks;

namespace DuetControlServer.Model
{
    /// <summary>
    /// Static helper class to merge the RepRapFirmware object model with ours
    /// </summary>
    public static class Updater
    {
        /// <summary>
        /// Merge received data into the object model
        /// </summary>
        /// <param name="module">Module that is supposed to be merged</param>
        /// <param name="json">JSON data</param>
        /// <returns>Asynchronous task</returns>
        public static async Task MergeData(byte module, string json)
        {
            JObject obj = JObject.Parse(json);

            using (await Provider.AccessReadWrite())
            {
                // TODO merge data. there is probably little point in this as long as no coordinates are reported..
                /*
                {
                    gcodes: {
                        speedFactor: 100.0
                    },
                    meshProbe: {
                        radius: -1.0
                    },
                    move: {
                        drcEnabled: "no",
                        drcMinimumAcceleration: 10.0,
                        drcPeriod: 50.0,
                        maxPrintingAcceleration: 10000.0,
                        maxTravelAcceleration: 10000.0
                    },
                    network: {
                        interfaces: [{
                            gateway: "0.0.0.0",
                            ip: "0.0.0.0",
                            name: "ethernet",
                            netmask: "0.0.0.0"
                        }]
                    },
                    randomProbe: {
                        numPointsProbed: 0
                    }
                }
                */
            }
        }
    }
}
