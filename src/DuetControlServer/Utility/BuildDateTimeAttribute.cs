using System;

namespace DuetControlServer.Utility
{
    /// <summary>
    /// Attribute to obtain the build datetime
    /// </summary>
    /// <param name="date"></param>
    [AttributeUsage(AttributeTargets.Assembly)]
    public class BuildDateTimeAttribute(string date) : Attribute
    {
        /// <summary>
        /// Build datetime
        /// </summary>
        public string Date { get; set; } = date;
    }
}
