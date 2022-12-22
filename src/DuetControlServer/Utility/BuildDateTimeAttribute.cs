using System;

namespace DuetControlServer.Utility
{
    /// <summary>
    /// Attribute to obtain the build datetime
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly)]
    public class BuildDateTimeAttribute : Attribute
    {
        /// <summary>
        /// Build datetime
        /// </summary>
        public string Date { get; set; }

        /// <summary>
        /// Constructor of this class
        /// </summary>
        /// <param name="date"></param>
        public BuildDateTimeAttribute(string date)
        {
            Date = date;
        }
    }
}
