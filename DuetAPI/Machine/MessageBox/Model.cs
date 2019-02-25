using System;

namespace DuetAPI.Machine.MessageBox
{
    /// <summary>
    /// Supported modes of displaying a message box
    /// </summary>
    public enum MessageBoxMode
    {
        /// <summary>
        /// Display a message box without any buttons
        /// </summary>
        NoButtons = 0,
        
        /// <summary>
        /// Display a message box with only a Close button
        /// </summary>
        CloseOnly,
        
        /// <summary>
        /// Display a message box with only an Ok button which is supposed to send M292 when pressed
        /// </summary>
        OkOnly,
        
        /// <summary>
        /// Display a message box with an Ok button that sends M292 P0 or a cancel button that sends M292 P1 when clicked
        /// </summary>
        OkCancel
    }

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