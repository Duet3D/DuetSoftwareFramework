using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.Utility
{
    /// <summary>
    /// Class representing a driver identifier
    /// </summary>
    [JsonConverter(typeof(DriverIdJsonConverter))]
    public sealed class DriverId : ICloneable
    {
        /// <summary>
        /// Default constructor of this class
        /// </summary>
        public DriverId() { }

        /// <summary>
        /// Constructor for creating a new instance from an unsigned integer
        /// </summary>
        /// <param name="value">Unsigned integer</param>
        public DriverId(uint value)
        {
            Board = (int)((value >> 16) & 0xFFFF);
            Port = (int)(value & 0xFFFF);
        }

        /// <summary>
        /// Constructor for creating a new instance from a board and a port
        /// </summary>
        /// <param name="board">Board number</param>
        /// <param name="port">Port number</param>
        public DriverId(int board, int port)
        {
            Board = board;
            Port = port;
        }

        /// <summary>
        /// Constructor for creating a new instance from a string
        /// </summary>
        /// <param name="value">String value</param>
        /// <exception cref="ArgumentException">Driver ID could not be parsed</exception>
        public DriverId(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                // Empty driver argument - keep defaults
                return;
            }

            string[] segments = value!.Split('.');
            if (segments.Length == 1)
            {
                if (ushort.TryParse(segments[0], out ushort port))
                {
                    Port = port;
                }
                else
                {
                    throw new ArgumentException($"Failed to parse driver number");
                }
            }
            else if (segments.Length == 2)
            {
                if (ushort.TryParse(segments[0], out ushort board))
                {
                    Board = board;
                }
                else
                {
                    throw new ArgumentException($"Failed to parse board number");
                }

                if (ushort.TryParse(segments[1], out ushort port))
                {
                    Port = port;
                }
                else
                {
                    throw new ArgumentException($"Failed to parse driver number");
                }
            }
        }

        /// <summary>
        /// Board of this driver identifier
        /// </summary>
        public int Board { get; set; }

        /// <summary>
        /// Port of this driver identifier
        /// </summary>
        public int Port { get; set; }

        /// <summary>
        /// Convert an instance to an unsigned integer as expected by RepRapFirmware
        /// </summary>
        /// <param name="id">Driver ID to convert</param>
        public static implicit operator uint(DriverId id) => (uint)((id.Board << 16) | id.Port);

        /// <summary>
        /// Convert an instance to a string
        /// </summary>
        /// <param name="id">Driver ID to convert</param>
        public static implicit operator string(DriverId id) => id.ToString();

        /// <summary>
        /// Compute a hash code for this instance
        /// </summary>
        /// <returns>Hash code</returns>
        public override int GetHashCode() => Board.GetHashCode() ^ Port.GetHashCode();

        /// <summary>
        /// Convert this instance to a string
        /// </summary>
        /// <returns>String representation</returns>
        public override string ToString() => $"{Board}.{Port}";

        /// <summary>
        /// Checks whether this instance is equal to another
        /// </summary>
        /// <param name="obj">Other instance</param>
        /// <returns>Whether this and the other instance are equal</returns>
        public override bool Equals(object? obj) => obj is DriverId other && Board == other.Board && Port == other.Port;

        /// <summary>
        /// Create a clome of this instance
        /// </summary>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public object Clone()
        {
            return new DriverId()
            {
                Board = Board,
                Port = Port
            };
        }
    }

    /// <summary>
    /// Converter for <see cref="DriverId"/> instances
    /// </summary>
    public sealed class DriverIdJsonConverter : JsonConverter<DriverId>
    {
        /// <summary>
        /// Read an instance from JSON
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="typeToConvert">Target type</param>
        /// <param name="options">JSON options</param>
        /// <returns></returns>
        public override DriverId? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.Null => null,
                JsonTokenType.String => new DriverId(reader.GetString()),
                _ => throw new JsonException("Invalid token type for DriverId"),
            };
        }

        /// <summary>
        /// Write an instance to JSON
        /// </summary>
        /// <param name="writer">JSON writer</param>
        /// <param name="value">Value to write</param>
        /// <param name="options">JSON options</param>
        public override void Write(Utf8JsonWriter writer, DriverId? value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStringValue(value.ToString());
            }
        }
    }
}
