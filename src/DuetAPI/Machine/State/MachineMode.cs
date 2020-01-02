using System.Text.Json.Serialization;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Possible operation modes of the machine
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MachineMode
    {
        /// <summary>
        /// Fused Filament Fabrication (default)
        /// </summary>
        FFF,
        
        /// <summary>
        /// Computer Numerical Control
        /// </summary>
        CNC,
        
        /// <summary>
        /// Laser operation mode (e.g. laser cutters)
        /// </summary>
        Laser
    }
}