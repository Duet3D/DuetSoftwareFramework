using DuetAPI.Utility;
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Enumeration of supported HTTP responses
    /// </summary>
    [JsonConverter(typeof(JsonCamelCaseStringEnumConverter<HttpResponseType>))]
    public enum HttpResponseType
    {
        /// <summary>
        /// HTTP status code without payload
        /// </summary>
        StatusCode,

        /// <summary>
        /// Plain text (UTF-8)
        /// </summary>
        PlainText,

        /// <summary>
        /// JSON-formatted data
        /// </summary>
        JSON,

        /// <summary>
        /// File content. Response must hold the absolute path to the file to return
        /// </summary>
        File,

        /// <summary>
        /// Send this request to another server (proxy). Response must hold the URI to send the request to
        /// </summary>
        URI
    }

#if NET9_0_OR_GREATER
#warning This converter will not be needed any more in .NET 9 and later. Better use JsonStringEnumMemberNameAttribute then
#endif

    /// <summary>
    /// Converter class for HttpResponseType
    /// </summary>
    public class HttpResponseTypeConverter : JsonConverter<HttpResponseType>
    {
        /// <summary>
        /// Read a JSON value and convert it to a HttpResponseType
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="typeToConvert">Target type</param>
        /// <param name="options">JSON options</param>
        /// <returns>Deserialized value</returns>
        public override HttpResponseType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("Expected string");
            }

            return reader.GetString() switch
            {
                "statuscode" => HttpResponseType.StatusCode,
                "plainText" => HttpResponseType.PlainText,
                "json" => HttpResponseType.JSON,
                "file" => HttpResponseType.File,
                "uri" => HttpResponseType.URI,
                _ => throw new JsonException("Unknown response type")
            };
        }

        /// <summary>
        /// Write a HttpResponseType value to JSON
        /// </summary>
        /// <param name="writer">JSON writer</param>
        /// <param name="value">Value to write</param>
        /// <param name="options">JSON options</param>
        public override void Write(Utf8JsonWriter writer, HttpResponseType value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value switch
            {
                HttpResponseType.StatusCode => "statuscode",
                HttpResponseType.PlainText => "plainText",
                HttpResponseType.JSON => "json",
                HttpResponseType.File => "file",
                HttpResponseType.URI => "uri",
                _ => throw new JsonException("Unknown response type")
            });
        }
    }

    /// <summary>
    /// Context for HttpResponseType serialization
    /// </summary>
    [JsonSerializable(typeof(HttpResponseType))]
    public partial class HttpResponseTypeContext : JsonSerializerContext { }
}
