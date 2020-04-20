using System;
using System.Text.Json;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about a filament monitor
    /// </summary>
    public class FilamentMonitor : ModelObject
    {
        /// <summary>
        /// Indicates if this filament monitor is enabled
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
			set => SetPropertyValue(ref _enabled, value);
        }
        private bool _enabled;

        /// <summary>
        /// Indicates if a filament is present
        /// </summary>
        public bool? FilamentPresent
        {
            get => _filamentPresent;
			set => SetPropertyValue(ref _filamentPresent, value);
        }
        private bool? _filamentPresent;

        /// <summary>
        /// Type of this filament monitor
        /// </summary>
        public FilamentMonitorType Type
        {
            get => _type;
			protected set => SetPropertyValue(ref _type, value);
        }
        private FilamentMonitorType _type = FilamentMonitorType.Unknown;

        /// <summary>
        /// Figure out the required type for the given filament monitor type
        /// </summary>
        /// <param name="type">Filament monitor type</param>
        /// <returns>Required type</returns>
        private Type GetFilamentMonitorType(FilamentMonitorType type)
        {
            return type switch
            {
                FilamentMonitorType.Laser => typeof(LaserFilamentMonitor),
                FilamentMonitorType.Pulsed => typeof(PulsedFilamentMonitor),
                FilamentMonitorType.RotatingMagnet => typeof(RotatingMagnetFilamentMonitor),
                _ => typeof(FilamentMonitor)
            };
        }

        /// <summary>
        /// Update this instance from a given JSON object
        /// </summary>
        /// <param name="jsonElement">JSON object</param>
        /// <returns>Updated instance</returns>
        public override ModelObject UpdateFromJson(JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (jsonElement.TryGetProperty("type", out JsonElement nameProperty))
            {
                FilamentMonitorType filamentMonitorType = (FilamentMonitorType)JsonSerializer.Deserialize(nameProperty.GetRawText(), typeof(FilamentMonitorType));
                Type requiredType = GetFilamentMonitorType(filamentMonitorType);
                if (GetType() != requiredType)
                {
                    FilamentMonitor newInstance = (FilamentMonitor)Activator.CreateInstance(requiredType);
                    newInstance.UpdateFromJson(jsonElement);
                    return newInstance;
                }
            }
            base.UpdateFromJson(jsonElement);
            return this;
        }
    }
}
