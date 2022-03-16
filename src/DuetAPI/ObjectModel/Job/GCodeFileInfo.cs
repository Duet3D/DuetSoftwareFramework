using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Holds information about a G-code file
    /// </summary>
    [JsonConverter(typeof(GCodeFileInfoConverter))]
    public sealed class GCodeFileInfo : ModelObject
    {
        /// <summary>
        /// Filament consumption per extruder drive (in mm)
        /// </summary>
        public ModelCollection<float> Filament { get; } = new ModelCollection<float>();

        /// <summary>
        /// The filename of the G-code file
        /// </summary>
        public string FileName
        {
            get => _fileName;
			set => SetPropertyValue(ref _fileName, value);
        }
        private string _fileName;

        /// <summary>
        /// Name of the application that generated this file
        /// </summary>
        public string GeneratedBy
        {
            get => _generatedBy;
			set => SetPropertyValue(ref _generatedBy, value);
        }
        private string _generatedBy;

        /// <summary>
        /// Build height of the G-code job or 0 if not found (in mm)
        /// </summary>
        public float Height
        {
            get => _height;
			set => SetPropertyValue(ref _height, value);
        }
        private float _height;

        /// <summary>
        /// Value indicating when the file was last modified or null if unknown
        /// </summary>
        [JsonConverter(typeof(Utility.JsonShortDateTimeConverter))]
        public DateTime? LastModified
        {
            get => _lastModified;
            set => SetPropertyValue(ref _lastModified, value);
        }
        private DateTime? _lastModified;

        /// <summary>
        /// Height of each other layer or 0 if not found (in mm)
        /// </summary>
        public float LayerHeight
        {
            get => _layerHeight;
			set => SetPropertyValue(ref _layerHeight, value);
        }
        private float _layerHeight;

        /// <summary>
        /// Number of total layers or 0 if unknown
        /// </summary>
        public int NumLayers
        {
            get => _numLayers;
			set => SetPropertyValue(ref _numLayers, value);
        }
        private int _numLayers;

        /// <summary>
        /// Estimated print time (in s)
        /// </summary>
        public long? PrintTime
        {
            get => _printTime;
			set => SetPropertyValue(ref _printTime, value);
        }
        private long? _printTime;
        
        /// <summary>
        /// Estimated print time from G-code simulation (in s)
        /// </summary>
        public long? SimulatedTime
        {
            get => _simulatedTime;
			set => SetPropertyValue(ref _simulatedTime, value);
        }
        private long? _simulatedTime;

        /// <summary>
        /// Size of the file
        /// </summary>
        public long Size
        {
            get => _size;
			set => SetPropertyValue(ref _size, value);
        }
        private long _size;

        /// <summary>
        /// Collection of thumbnails parsed from Gcode
        /// </summary>
        [SbcProperty(true)]
        public ModelCollection<ThumbnailInfo> Thumbnails { get; } = new ModelCollection<ThumbnailInfo>();
    }

    /// <summary>
    /// Class used to convert G-code file info to and from JSON
    /// </summary>
    public class GCodeFileInfoConverter : JsonConverter<GCodeFileInfo>
    {
        /// <summary>
        /// Read a parsed file info object from a JSON reader
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="typeToConvert">Target type</param>
        /// <param name="options">JSON options</param>
        /// <returns>Parsed file info object</returns>
        public override GCodeFileInfo Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using JsonDocument jsonDocument = JsonDocument.ParseValue(ref reader);
            if (jsonDocument.RootElement.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            GCodeFileInfo parsedFileInfo = new GCodeFileInfo();
            parsedFileInfo.UpdateFromJson(jsonDocument.RootElement, false);
            return parsedFileInfo;
        }

        /// <summary>
        /// Write a parsed file info object to a JSON writer
        /// </summary>
        /// <param name="writer">JSON writer</param>
        /// <param name="value">Machine model</param>
        /// <param name="options">JSON options</param>
        public override void Write(Utf8JsonWriter writer, GCodeFileInfo value, JsonSerializerOptions options)
        {
            if (value == null)
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