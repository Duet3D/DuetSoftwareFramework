using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Base class for filament monitors
    /// </summary>
    [JsonDerivedType(typeof(LaserFilamentMonitor))]
    [JsonDerivedType(typeof(PulsedFilamentMonitor))]
    [JsonDerivedType(typeof(RotatingMagnetFilamentMonitor))]
    public partial class FilamentMonitor : ModelObject, IDynamicModelObject
    {
        /// <summary>
        /// Indicates if this filament monitor is enabled
        /// </summary>
        [Obsolete("Use EnableMode instead")]
        public bool Enabled
        {
            get => _enabled;
			set => SetPropertyValue(ref _enabled, value);
        }
        private bool _enabled;

        /// <summary>
        /// Enable mode of this filament monitor
        /// </summary>
        public FilamentMonitorEnableMode EnableMode
        {
            get => _enableMode;
            set => SetPropertyValue(ref _enableMode, value);
        }
        private FilamentMonitorEnableMode _enableMode = FilamentMonitorEnableMode.Disabled;

        /// <summary>
        /// Last reported status of this filament monitor
        /// </summary>
        public FilamentMonitorStatus Status
        {
            get => _status;
            set => SetPropertyValue(ref _status, value);
        }
        private FilamentMonitorStatus _status = FilamentMonitorStatus.NoDataReceived;

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
        /// Update this instance from a given JSON element
        /// </summary>
        /// <param type="jsonElement">Element to update this intance from</param>
        /// <param type="ignoreSbcProperties">Whether SBC properties are ignored</param>
        /// <returns>Updated instance</returns>
        /// <exception cref="JsonException">Failed to deserialize data</exception>
        public IDynamicModelObject? UpdateFromJson(JsonElement jsonElement, bool ignoreSbcProperties)
        {
            if (jsonElement.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (jsonElement.TryGetProperty("type", out JsonElement nameProperty))
            {
                string? type = nameProperty.GetString();
                if (type is "laser")
                {
                    if (this is not LaserFilamentMonitor)
                    {
                        FilamentMonitor newInstance = new LaserFilamentMonitor();
                        return newInstance.UpdateFromJson(jsonElement, ignoreSbcProperties);
                    }
                }
                else if (type is "pulsed")
                {
                    if (this is not PulsedFilamentMonitor)
                    {
                        FilamentMonitor newInstance = new PulsedFilamentMonitor();
                        return newInstance.UpdateFromJson(jsonElement, ignoreSbcProperties);
                    }
                }
                else if (type is "rotatingMagnet")
                {
                    if (this is not RotatingMagnetFilamentMonitor)
                    {
                        FilamentMonitor newInstance = new RotatingMagnetFilamentMonitor();
                        return newInstance.UpdateFromJson(jsonElement, ignoreSbcProperties);
                    }
                }
                else if (this is LaserFilamentMonitor or PulsedFilamentMonitor or RotatingMagnetFilamentMonitor)
                {
                    FilamentMonitor newInstance = new();
                    return newInstance.UpdateFromJson(jsonElement, ignoreSbcProperties);
                }
            }
            return GeneratedUpdateFromJson(jsonElement, ignoreSbcProperties);
        }


        /// <summary>
        /// Update this instance from a given JSON reader
        /// </summary>
        /// <param type="reader">JSON reader</param>
        /// <param type="ignoreSbcProperties">Whether SBC properties are ignored</param>
        /// <returns>Updated instance</returns>
        /// <exception cref="JsonException">Failed to deserialize data</exception>
        public IDynamicModelObject? UpdateFromJsonReader(ref Utf8JsonReader reader, bool ignoreSbcProperties)
        {
            if (reader.TokenType == JsonTokenType.None && !reader.Read())
            {
                throw new JsonException("failed to read from JSON reader");
            }
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException("expected start of object");
            }

            Utf8JsonReader readerCopy = reader;
            while (readerCopy.Read() && readerCopy.TokenType != JsonTokenType.EndObject)
            {
                if (readerCopy.TokenType == JsonTokenType.PropertyName)
                {
                    if (readerCopy.ValueTextEquals("type"u8) && readerCopy.Read())
                    {
                        string? type = readerCopy.GetString();
                        if (type is "laser")
                        {
                            if (this is not LaserFilamentMonitor)
                            {
                                FilamentMonitor newInstance = new LaserFilamentMonitor();
                                return newInstance.UpdateFromJsonReader(ref reader, ignoreSbcProperties);
                            }
                        }
                        else if (type is "pulsed")
                        {
                            if (this is not PulsedFilamentMonitor)
                            {
                                FilamentMonitor newInstance = new PulsedFilamentMonitor();
                                return newInstance.UpdateFromJsonReader(ref reader, ignoreSbcProperties);
                            }
                        }
                        else if (type is "rotatingMagnet")
                        {
                            if (this is not RotatingMagnetFilamentMonitor)
                            {
                                FilamentMonitor newInstance = new RotatingMagnetFilamentMonitor();
                                return newInstance.UpdateFromJsonReader(ref reader, ignoreSbcProperties);
                            }
                        }
                        else if (this is LaserFilamentMonitor or PulsedFilamentMonitor or RotatingMagnetFilamentMonitor)
                        {
                            FilamentMonitor newInstance = new();
                            return newInstance.UpdateFromJsonReader(ref reader, ignoreSbcProperties);
                        }
                    }
                }
                else if (readerCopy.TokenType == JsonTokenType.StartObject)
                {
                    readerCopy.Skip();
                }
            }
            return GeneratedUpdateFromJsonReader(ref reader, ignoreSbcProperties);
        }
    }
}
