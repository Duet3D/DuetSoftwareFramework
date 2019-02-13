using System;

namespace DuetAPI.Machine.State
{
    // TODO turn the string enums into real enums and convert them via dedicate attributes on serialization
    public static class Mode
    {
        public const string FFF = "FFF";
        public const string CNC = "CNC";
        public const string Laser = "Laser";
    }

    public static class Status
    {
        public const string Updating = "updating";
        public const string Off = "off";
        public const string Halted = "halted";
        public const string Pausing = "pausing";
        public const string Paused = "paused";
        public const string Resuming = "resuming";
        public const string Processing = "processing";
        public const string Simulating = "simulating";
        public const string Busy = "busy";
        public const string ChangingTool = "changingTool";
        public const string Idle = "idle";
    }

    public class Model : ICloneable
    {
        public bool AtxPower { get; set; }
        public int CurrentTool { get; set; } = -1;                      // -1 if none selected
        public string Mode { get; set; } = State.Mode.FFF;              // one of Mode
        public bool RelativeExtrusion { get; set; }
        public bool RelativePositioning { get; set; }
        public string Status { get; set; } = State.Status.Idle;         // one of Status

        public object Clone()
        {
            return new Model
            {
                AtxPower = AtxPower,
                CurrentTool = CurrentTool,
                Mode = (Mode != null) ? string.Copy(Mode) : null,
                RelativeExtrusion = RelativeExtrusion,
                RelativePositioning = RelativePositioning,
                Status = (Status != null) ? string.Copy(Status) : null
            };
        }
    }
}