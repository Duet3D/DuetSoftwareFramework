﻿using DuetAPI.Machine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DuetAPI.Utility
{
    /// <summary>
    /// JSON converter to read and write a list of regular expressions
    /// </summary>
    /// <remarks>
    /// This class may become obsolete in a future .NET Core version.
    /// For some reason it has no effect to add this converter to the default JSON options.
    /// </remarks>
    public class JsonRegexListConverter : JsonConverter<List<Regex>>
    {
        /// <summary>
        /// Read a Regex list from JSON
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="typeToConvert">Type to convert</param>
        /// <param name="options">Reader options</param>
        /// <returns>Read value</returns>
        public override List<Regex> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.StartArray)
            {
                List<Regex> regexList = new List<Regex>();

                string pattern = null;
                int optionsValue = 0;
                string propertyName = string.Empty;

                while (reader.Read())
                {
                    switch (reader.TokenType)
                    {
                        case JsonTokenType.StartObject:
                            propertyName = string.Empty;
                            pattern = null;
                            optionsValue = 0;
                            break;

                        case JsonTokenType.PropertyName:
                            propertyName = reader.GetString();
                            break;

                        case JsonTokenType.String:
                            if (propertyName.Equals("Pattern", StringComparison.InvariantCultureIgnoreCase))
                            {
                                pattern = reader.GetString();
                            }
                            break;

                        case JsonTokenType.Number:
                            if (propertyName.Equals("Options", StringComparison.InvariantCultureIgnoreCase))
                            {
                                optionsValue = reader.GetInt32();
                            }
                            break;

                        case JsonTokenType.EndObject:
                            regexList.Add(new Regex(pattern, (RegexOptions)optionsValue));
                            break;

                        case JsonTokenType.EndArray:
                            return regexList;
                    }
                }
            }

            throw new JsonException("Invalid regular expression");
        }

        /// <summary>
        /// Write a Regex list to JSON
        /// </summary>
        /// <param name="writer">JSON writer</param>
        /// <param name="value">Value to serialize</param>
        /// <param name="options">Write options</param>
        public override void Write(Utf8JsonWriter writer, List<Regex> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (Regex regex in value)
            {
                writer.WriteStartObject();
                writer.WriteString("Pattern", regex.ToString());
                writer.WriteNumber("Options", (int)regex.Options);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
    }

    /// <summary>
    /// Helper class for JSON serialization, deserialization, patch creation and patch application
    /// </summary>
    public static class JsonHelper
    {
        /// <summary>
        /// Default JSON (de-)serialization options
        /// </summary>
        public static readonly JsonSerializerOptions DefaultJsonOptions = new JsonSerializerOptions
        {
            Converters = {
                new JsonPolymorphicWriteOnlyConverter<Kinematics>(),
                new JsonPolymorphicWriteOnlyConverter<FilamentMonitor>()
            },
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Default JSON (de-)serialization options without converters
        /// </summary>
        public static readonly JsonSerializerOptions DefaultJsonOptionsNoConverters = new JsonSerializerOptions
        {
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Receive a serialized JSON object from a socket in UTF-8 format
        /// </summary>
        /// <param name="socket">Socket to read from</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Plain JSON</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        public static async Task<MemoryStream> ReceiveUtf8Json(Socket socket, CancellationToken cancellationToken = default)
        {
            MemoryStream jsonStream = new MemoryStream();
            bool inJson = false, inQuotes = false, isEscaped = false;
            int numBraces = 0;

            byte[] readData = new byte[1];
            while (!inJson || numBraces > 0)
            {
                if (await socket.ReceiveAsync(readData, SocketFlags.None, cancellationToken) <= 0)
                {
                    // Do not keep reading if the connection has been gracefully closed
                    jsonStream.Dispose();
                    throw new SocketException((int)SocketError.NotConnected);
                }

                char c = (char)readData[0];
                if (inQuotes)
                {
                    if (isEscaped)
                    {
                        isEscaped = false;
                    }
                    else if (c == '\\')
                    {
                        isEscaped = true;
                    }
                    else if (c == '"')
                    {
                        inQuotes = false;
                    }
                }
                else if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == '{')
                {
                    inJson = true;
                    numBraces++;
                }
                else if (c == '}')
                {
                    numBraces--;
                }

                if (inJson)
                {
                    jsonStream.WriteByte(readData[0]);
                }
            }

            jsonStream.Seek(0, SeekOrigin.Begin);
            return jsonStream;
        }
    }
}
