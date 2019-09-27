using System;
using System.Text.Json.Serialization;

namespace DuetAPI.Utility
{
    /// <summary>
    /// Flags a property so that a JSON patch creates only added items or an empty list in case it is cleared
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class JsonGrowingListAttribute  : JsonAttribute
    {
        /// <summary>
        /// Creates a new instance of this type
        /// </summary>
        public JsonGrowingListAttribute() { }
    }
}
