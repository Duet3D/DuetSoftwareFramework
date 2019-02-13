using System;

namespace DuetAPI.Machine.MessageBox
{
    public enum MessageBoxMode
    {
        NoButtons = 0,
        CloseOnly,
        OkOnly,
        OkCancel
    }

    public class Model : ICloneable
    {
        public MessageBoxMode? Mode { get; set; }
        public string Title { get; set; }
        public string Message { get; set; }
        public uint[] AxisControls { get; set; } = new uint[0];         // indices

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