using System;

namespace DuetAPI.Machine.MessageBox
{
    /// <summary>
    /// Information about the message box to show
    /// </summary>
    public class Model : ICloneable
    {
        /// <summary>
        /// Mode of the message box to display or null if none is shown
        /// </summary>
        public MessageBoxMode? Mode { get; set; }
        
        /// <summary>
        /// Title of the message box
        /// </summary>
        public string Title { get; set; }
        
        /// <summary>
        /// Content of the message box
        /// </summary>
        public string Message { get; set; }
        
        /// <summary>
        /// Optional axis movement controls to show (axis indices)
        /// </summary>
        public uint[] AxisControls { get; set; } = new uint[0];

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Model
            {
                Mode = Mode,
                Title = (Title != null) ? string.Copy(Title) : null,
                Message = (Message != null) ? string.Copy(Message) : null,
                AxisControls = (uint[])AxisControls.Clone()
            };
        }
    }
}