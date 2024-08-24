using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Possible operation modes of the machine
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<MachineMode>))]
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

    /// <summary>
    /// Context for MachineMode serialization
    /// </summary>
    [JsonSerializable(typeof(MachineMode))]
    public partial class MachineModeContext : JsonSerializerContext { }
}