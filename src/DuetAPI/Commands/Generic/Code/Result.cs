using DuetAPI.ObjectModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.Commands
{
    /// <summary>
    /// List-based representation of a code result.
    /// Each item represents a <see cref="Message"/> instance which can be easily converted to a string
    /// </summary>
    /// <remarks>
    /// This class is now deprecated. It will be replaced with <see cref="Message"/> in foreseeable future
    /// </remarks>
    public sealed class CodeResult : List<Message>
    {
        /// <summary>
        /// Create a new code result indicating success
        /// </summary>
        public CodeResult() { }

        /// <summary>
        /// Create a new code result with an initial message
        /// </summary>
        /// <param name="type">Message type</param>
        /// <param name="content">Message content</param>
        public CodeResult(MessageType type, string content) => Add(type, content);

        /// <summary>
        /// Add another message
        /// </summary>
        /// <param name="type">Message type</param>
        /// <param name="content">Message content</param>
        public void Add(MessageType type, string content) => Add(new Message(type, content));

        /// <summary>
        /// Checks if the message contains any data
        /// </summary>
        public bool IsEmpty => !this.Any(item => !string.IsNullOrEmpty(item.Content));

        /// <summary>
        /// Indicates if the code could complete without an error
        /// </summary>
        public bool IsSuccessful => !this.Any(item => item.Type == MessageType.Error);

        /// <summary>
        /// Converts the CodeResult to a string
        /// </summary>
        /// <returns>The CodeResult as a string</returns>
        public override string ToString() => string.Join(System.Environment.NewLine, this);
    }

    /// <summary>
    /// JSON converter for a <see cref="CodeResult"/>
    /// </summary>
    public sealed class CodeResultConverter : JsonConverter<CodeResult>
    {
        /// <summary>
        /// Read a code result or message from JSON
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="typeToConvert">Target type</param>
        /// <param name="options">Serializer options</param>
        /// <returns>Deserialized code result</returns>
        public override CodeResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                Message msg = JsonSerializer.Deserialize<Message>(ref reader, options);
                return new CodeResult { msg };
            }
            return (CodeResult)JsonSerializer.Deserialize<List<Message>>(ref reader, options);
        }

        /// <summary>
        /// Write a code result or message to JSON
        /// </summary>
        /// <param name="writer">JSON writer</param>
        /// <param name="value">Value to write</param>
        /// <param name="options">Serializer options</param>
        public override void Write(Utf8JsonWriter writer, CodeResult value, JsonSerializerOptions options)
        {
            if (value == null)
            {
                writer.WriteNullValue();
            }
            else if (value.Count == 1)
            {
                JsonSerializer.Serialize(value[0], options);
            }
            else
            {
                JsonSerializer.Serialize<List<Message>>(writer, value, options);
            }
        }
    }
}
