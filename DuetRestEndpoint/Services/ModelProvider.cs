using Newtonsoft.Json.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DuetRestEndpoint.Services
{
    /// <summary>
    /// Static interface for retrieving and updating the serialized machine model
    /// </summary>
    public static class ModelProvider
    {
        private static JObject _jsonModel = JObject.FromObject(new DuetAPI.Machine.Model());
        private static AutoResetEvent _updateEvent = new AutoResetEvent(false);

        /// <summary>
        /// This indicates if a connection could be established by the ModelService class
        /// </summary>
        public static bool IsConnected;

        /// <summary>
        /// Assign initial state of the object model.
        /// </summary>
        /// <remarks>May be only called when the full machine model is initially received</remarks>
        /// <param name="newModel">Full machine model</param>
        public static void Set(JObject newModel)
        {
            lock (_jsonModel)
            {
                _jsonModel.ReplaceAll(newModel);
            }
        }

        /// <summary>
        /// Update the full machine model with a chunk of new data
        /// </summary>
        /// <param name="diff">Difference to the last state</param>
        public static void Update(JObject diff)
        {
            lock (_jsonModel)
            {
                DuetAPI.JsonHelper.PatchObject(_jsonModel, diff);

                _updateEvent.Set();
                _updateEvent.Reset();
            }
        }

        /// <summary>
        /// Retrieve a full object model instance. This creates a clone to avoid race conditions
        /// </summary>
        /// <returns>Full object model of the machine</returns>
        public static JObject GetFull()
        {
            lock (_jsonModel)
            {
                return (JObject)_jsonModel.DeepClone();
            }
        }

        /// <summary>
        /// Wait for an update to occur
        /// </summary>
        /// <returns>Task that completes when an update has occurred</returns>
        public static Task WaitForUpdate(CancellationToken cancellationToken = default(CancellationToken)) => Task.Run(() => _updateEvent.WaitOne(), cancellationToken);

        /// <summary>
        /// Retrieve a partial JSON patch since the last state represented by oldModel.
        /// In addition the object in oldModel is updated with the current state
        /// </summary>
        /// <param name="oldModel">Last known object model state</param>
        /// <returns>JSON patch representing the differences between the last and new states</returns>
        public static JObject GetPatch(ref JObject oldModel)
        {
            lock (_jsonModel)
            {
                JObject diff = DuetAPI.JsonHelper.DiffObject(oldModel, _jsonModel);
                oldModel.Merge(_jsonModel);
                return diff;
            }
        }
    }
}
