using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Class representing a loaded plugin
    /// </summary>
    [JsonConverter(typeof(PluginConverter))]
    public sealed class Plugin : PluginManifest
    {
        /// <summary>
        /// List of files for the DSF plugin
        /// </summary>
        public ModelCollection<string> DsfFiles { get; } = new ModelCollection<string>();

        /// <summary>
        /// List of files for the DWC plugin
        /// </summary>
        public ModelCollection<string> DwcFiles { get; } = new ModelCollection<string>();

        /// <summary>
        /// List of files to be installed to the (virtual) SD excluding web files
        /// </summary>
        public ModelCollection<string> SdFiles { get; } = new ModelCollection<string>();

        /// <summary>
        /// Process ID of the plugin or -1 if not started. It is set to 0 while the plugin is being shut down
        /// </summary>
        public int Pid
        {
            get => _pid;
            set => SetPropertyValue(ref _pid, value);
        }
        private int _pid = -1;
    }

    /// <summary>
    /// Class used to convert plugins to and from JSON
    /// </summary>
    public class PluginConverter : JsonConverter<Plugin>
    {
        /// <summary>
        /// Read a machine model object from a JSON reader
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="typeToConvert">Target type</param>
        /// <param name="options">JSON options</param>
        /// <returns>Plugin</returns>
        public override Plugin? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using JsonDocument jsonDocument = JsonDocument.ParseValue(ref reader);
            if (jsonDocument.RootElement.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            Plugin plugin = new();
            plugin.UpdateFromJson(jsonDocument.RootElement, false);
            return plugin;
        }

        /// <summary>
        /// Write a plugin to a JSON writer
        /// </summary>
        /// <param name="writer">JSON writer</param>
        /// <param name="value">Plugin</param>
        /// <param name="options">JSON options</param>
        public override void Write(Utf8JsonWriter writer, Plugin value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartObject();
                foreach (KeyValuePair<string, PropertyInfo> jsonProperty in value.JsonProperties)
                {
                    writer.WritePropertyName(jsonProperty.Key);
                    JsonSerializer.Serialize(writer, jsonProperty.Value.GetValue(value), jsonProperty.Value.PropertyType, options);
                }
                writer.WriteEndObject();
            }
        }
    }
}
