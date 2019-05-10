using System;
using System.Collections.Generic;
using System.Linq;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the electronics used
    /// </summary>
    public class Electronics : ICloneable
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
        public MinMaxCurrent<float?> VIn { get; set; } = new MinMaxCurrent<float?>();
        
        /// <summary>
        /// MCU temperature details of the main board (in degC or null if unknown)
        /// </summary>
        public MinMaxCurrent<float?> McuTemp { get; set; } = new MinMaxCurrent<float?>();
        
        /// <summary>
        /// Information about attached expansion boards
        /// </summary>
        ///<seealso cref="ExpansionBoard"/>
        public List<ExpansionBoard> ExpansionBoards { get; set; } = new List<ExpansionBoard>();

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Electronics
            {
                Type = (Type != null) ? string.Copy(Type) : null,
                Name = (Name != null) ? string.Copy(Name) : null,
                Revision = (Revision != null) ? string.Copy(Revision) : null,
                Firmware = (Firmware)Firmware.Clone(),
                ProcessorID = (ProcessorID != null) ? string.Copy(ProcessorID) : null,
                VIn = (MinMaxCurrent<float?>)VIn.Clone(),
                McuTemp = (MinMaxCurrent<float?>)McuTemp.Clone(),
                ExpansionBoards = ExpansionBoards.Select(board => (ExpansionBoard)board.Clone()).ToList()
            };
        }
    }
}
