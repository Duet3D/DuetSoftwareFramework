using System;

namespace DuetAPI.Utility
{
    /// <summary>
    /// Class representing a driver identifier
    /// </summary>
    public sealed class DriverId
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
        public DriverId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                // Empty driver argument - keep defaults
                return;
            }

            string[] segments = value.Split('.');
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
        /// Checks if two instances are equal
        /// </summary>
        /// <param name="obj"></param>
        /// <returns></returns>
        public override bool Equals(object obj)
        {
            if (obj != null && obj is DriverId other)
            {
                return (Board == other.Board) && (Port == other.Port);
            }
            return false;
        }

        /// <summary>
        /// Compute a hash code for this instance
        /// </summary>
        /// <returns>Hash code</returns>
        public override int GetHashCode() => HashCode.Combine(Board.GetHashCode(), Port.GetHashCode());

        /// <summary>
        /// Convert this instance to a string
        /// </summary>
        /// <returns>String representation</returns>
        public override string ToString() => $"{Board}.{Port}";
    }
}
