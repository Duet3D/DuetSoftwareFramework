using System;
using System.Collections.Generic;
using System.Linq;

namespace DuetAPI.Machine.Electronics
{
    public class Firmware : ICloneable
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public string Date { get; set; }

        public object Clone()
        {
            return new Firmware
            {
                Name = (Name != null) ? string.Copy(Name) : null,
                Version = (Version != null) ? string.Copy(Version) : null,
                Date = (Date != null) ? string.Copy(Date) : null
            };
        }
    }

    public class MinMaxCurrent<T> : ICloneable
    {
        public T Current { get; set; }
        public T Min { get; set; }
        public T Max { get; set; }

        public object Clone()
        {
            return new MinMaxCurrent<T>
            {
                Current = Current,
                Min = Min,
                Max = Max
            };
        }
    }

    public class Model : ICloneable
    {
        public string Type { get; set; }
        public string Name { get; set; }
        public string Revision { get; set; }
        public Firmware Firmware { get; set; } = new Firmware();
        public string ProcessorID { get; set; }
        public MinMaxCurrent<double?> VIn { get; set; } = new MinMaxCurrent<double?>();
        public MinMaxCurrent<double?> McuTemp { get; set; } = new MinMaxCurrent<double?>();
        public List<ExpansionBoard> ExpansionBoards { get; set; } = new List<ExpansionBoard>();

        public object Clone()
        {
            return new Model
            {
                Type = (Type != null) ?  string.Copy(Type) : null,
                Name = (Name != null) ? string.Copy(Name) : null,
                Revision = (Revision != null) ? string.Copy(Revision) : null,
                Firmware = (Firmware)Firmware.Clone(),
                ProcessorID = (ProcessorID != null) ? string.Copy(ProcessorID) : null,
                VIn = (MinMaxCurrent<double?>)VIn.Clone(),
                McuTemp = (MinMaxCurrent<double?>)McuTemp.Clone(),
                ExpansionBoards = ExpansionBoards.Select(board => (ExpansionBoard)board.Clone()).ToList()
            };
        }
    }
}
