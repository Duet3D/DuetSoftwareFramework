using System;
using System.Collections.Generic;
using System.Linq;

namespace DuetAPI.Machine.Electronics
{
    /// <summary>
    /// Information about the electronics used
    /// </summary>
    public class Model : ICloneable
    {
        /// <summary>
        /// Type name of the main board
        /// </summary>
        public string Type { get; set; }
        
        /// <summary>
        /// Full name of the main board
        /// </summary>
        public string Name { get; set; }
        
        /// <summary>
        /// Revision of the main board
        /// </summary>
        public string Revision { get; set; }
        
        /// <summary>
        /// Main firmware of the attached main board
        /// </summary>
        public Firmware Firmware { get; set; } = new Firmware();
        
        /// <summary>
        /// Processor ID of the main board
        /// </summary>
        public string ProcessorID { get; set; }
        
        /// <summary>
        /// Input voltage details of the main board (in V or null if unknown)
        /// </summary>
        public MinMaxCurrent<double?> VIn { get; set; } = new MinMaxCurrent<double?>();
        
        /// <summary>
        /// MCU temperature details of the main board (in degC or null if unknown)
        /// </summary>
        public MinMaxCurrent<double?> McuTemp { get; set; } = new MinMaxCurrent<double?>();
        
        /// <summary>
        /// Information about attached expansion boards
        /// </summary>
        public List<ExpansionBoard> ExpansionBoards { get; set; } = new List<ExpansionBoard>();

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Model
            {
                Type = (Type != null) ? string.Copy(Type) : null,
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
