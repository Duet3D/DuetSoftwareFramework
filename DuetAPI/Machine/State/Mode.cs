using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace DuetAPI.Machine.State
{
    /// <summary>
    /// Possible operation modes of the machine
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum Mode
    {
        /// <summary>
        /// Filament Fused Fabrication (default)
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