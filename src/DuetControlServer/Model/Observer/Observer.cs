using DuetAPI.ObjectModel;
using System;
using System.Text.Json;

namespace DuetControlServer.Model
{
    /// <summary>
    /// Main class for observing changes in the machine model
    /// </summary>
    public static partial class Observer
    {
        /// <summary>
        /// Delegate to call when a property is being changed
        /// </summary>
        /// <param name="path">Path to the value that changed</param>
        /// <param name="changeType">Type of the modification</param>
        /// <param name="value">New value</param>
        public delegate void PropertyPathChanged(object[] path, PropertyChangeType changeType, object value);

        /// <summary>
        /// Event to call when an objet model value has been changed
        /// </summary>
        public static event PropertyPathChanged OnPropertyPathChanged;

        /// <summary>
        /// Initializes the observer to keep track of deep changes in the object model
        /// </summary>
        public static void Init() => SubscribeToModelObject(Provider.Get, Array.Empty<object>());

        /// <summary>
        /// Add a new element to a property path
        /// </summary>
        /// <param name="path">Existing path</param>
        /// <param name="toAdd">Element(s) to add</param>
        /// <returns>Combined property path</returns>
        private static object[] AddToPath(object[] path, params object[] toAdd)
        {
            object[] newPath = new object[path.Length + toAdd.Length];
            path.CopyTo(newPath, 0);
            toAdd.CopyTo(newPath, path.Length);
            return newPath;
        }

        /// <summary>
        /// Notify subscribers that the list of global variables has been reset
        /// </summary>
        public static void GlobalVariablesCleared()
        {
            string jsonName = JsonNamingPolicy.CamelCase.ConvertName(nameof(ObjectModel.Global));
            OnPropertyPathChanged?.Invoke(new object[] { jsonName }, PropertyChangeType.Property, null);
        }
    }
}
