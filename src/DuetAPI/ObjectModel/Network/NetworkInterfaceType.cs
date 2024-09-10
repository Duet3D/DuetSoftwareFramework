using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Supported types of network interfaces
    /// </summary>
    [JsonConverter(typeof(HttpResponseTypeConverter))]
    public enum NetworkInterfaceType
    {
        /// <summary>
        /// Wired network interface
        /// </summary>
        LAN,

        /// <summary>
        /// Wireless network interface
        /// </summary>
        WiFi
    }

#if NET9_0_OR_GREATER
#warning This converter will not be needed any more in .NET 9 and later. Better use JsonStringEnumMemberNameAttribute then
#endif

    /// <summary>
    /// Converter class for HttpResponseType
    /// </summary>
    public class HttpResponseTypeConverter : JsonConverter<NetworkInterfaceType>
    {
        /// <summary>
        /// Read a JSON value and convert it to a HttpResponseType
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="typeToConvert">Target type</param>
        /// <param name="options">JSON options</param>
        /// <returns>Deserialized value</returns>
        public override NetworkInterfaceType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("Expected string");
            }

            return reader.GetString() switch
            {
                "lan" => NetworkInterfaceType.LAN,
                "wifi" => NetworkInterfaceType.WiFi,
                _ => throw new JsonException("Unknown network interface type")
            };
        }

        /// <summary>
        /// Write a HttpResponseType value to JSON
        /// </summary>
        /// <param name="writer">JSON writer</param>
        /// <param name="value">Value to write</param>
        /// <param name="options">JSON options</param>
        public override void Write(Utf8JsonWriter writer, NetworkInterfaceType value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value switch
            {
                NetworkInterfaceType.LAN => "lan",
                NetworkInterfaceType.WiFi => "wifi",
                _ => throw new JsonException("Unknown network interface type")
            });
        }
    }
}
