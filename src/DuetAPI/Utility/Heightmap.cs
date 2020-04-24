﻿using DuetAPI.Machine;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace DuetAPI.Utility
{
    /// <summary>
    /// Class representing a heightmap
    /// </summary>
    public sealed class Heightmap
    {
        /// <summary>
        /// X start coordinate of the heightmap
        /// </summary>
        public float XMin { get; set; }

        /// <summary>
        /// X end coordinate of the heightmap
        /// </summary>
        public float XMax { get; set; }

        /// <summary>
        /// Spacing between the probe points in X direction
        /// </summary>
        public float XSpacing { get; set; }

        /// <summary>
        /// Y start coordinate of the heightmap
        /// </summary>
        public float YMin { get; set; }

        /// <summary>
        /// Y end coordinate of the heightmap
        /// </summary>
        public float YMax { get; set; }

        /// <summary>
        /// Spacing between the probe points in Y direction
        /// </summary>
        public float YSpacing { get; set; }

        /// <summary>
        /// Probing radius on delta geometries
        /// </summary>
        public float Radius { get; set; }

        /// <summary>
        /// Number of probe points in X direction
        /// </summary>
        public int NumX { get; set; }

        /// <summary>
        /// Number of probe points in Y direction
        /// </summary>
        public int NumY { get; set; }

        /// <summary>
        /// Z coordinate of each probe point
        /// </summary>
        public float[] ZCoordinates { get; set; }

        /// <summary>
        /// Load a new heightmap from the given CSV file
        /// </summary>
        /// <param name="filename">Path to the file</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="IOException">Invalid file</exception>
        public async Task Load(string filename)
        {
            using FileStream stream = new FileStream(filename, FileMode.Open, FileAccess.Read);
            using StreamReader reader = new StreamReader(stream);

            string line = await reader.ReadLineAsync();
            if (!line.StartsWith("RepRapFirmware height map file v2 generated at"))
            {
                throw new IOException("Invalid file format");
            }

            string[] columns = (await reader.ReadLineAsync()).Split(',');
            string[] values = (await reader.ReadLineAsync()).Split(',');
            if (columns.Length != values.Length)
            {
                throw new IOException("Invalid number of columns and values");
            }

            // Read grid definition
            for (int i = 0; i < columns.Length; i++)
            {
                string column = columns[i], value = values[i];
                switch (column)
                {
                    case "xmin":
                        XMin = float.Parse(value, NumberStyles.Any, CultureInfo.InvariantCulture);
                        break;
                    case "xmax":
                        XMax = float.Parse(value, NumberStyles.Any, CultureInfo.InvariantCulture);
                        break;
                    case "ymin":
                        YMin = float.Parse(value, NumberStyles.Any, CultureInfo.InvariantCulture);
                        break;
                    case "ymax":
                        YMax = float.Parse(value, NumberStyles.Any, CultureInfo.InvariantCulture);
                        break;
                    case "radius":
                        Radius = float.Parse(value, NumberStyles.Any, CultureInfo.InvariantCulture);
                        break;
                    case "xspacing":
                        XSpacing = float.Parse(value, NumberStyles.Any, CultureInfo.InvariantCulture);
                        break;
                    case "yspacing":
                        YSpacing = float.Parse(value, NumberStyles.Any, CultureInfo.InvariantCulture);
                        break;
                    case "xnum":
                        NumX = int.Parse(value);
                        break;
                    case "ynum":
                        NumY = int.Parse(value);
                        break;
                    default:
                        throw new IOException($"Unknown heightmap field: {column}");
                }
            }

            // Create array of points
            ZCoordinates = new float[NumX * NumY];

            // Read values in Y direction
            int index = 0;
            for (int y = 0; y < NumY; y++)
            {
                line = await reader.ReadLineAsync();

                // Read values in X direction
                values = line.Split(',').Select(val => val.Trim()).ToArray();
                for (int x = 0; x < NumX; x++)
                {
                    ZCoordinates[index++] = (values[x] == "0") ? float.NaN : float.Parse(values[x], NumberStyles.Any, CultureInfo.InvariantCulture);
                }
            }
        }

        /// <summary>
        /// Save a heightmap to the given CSV file
        /// </summary>
        /// <param name="filename">Filename of the heightmap</param>
        /// <returns>Asynchronous task</returns>
        public async Task Save(string filename)
        {
            using FileStream stream = new FileStream(filename, FileMode.Create, FileAccess.Write);
            using StreamWriter writer = new StreamWriter(stream);

            await writer.WriteLineAsync($"RepRapFirmware height map file v2 generated at {DateTime.Now:yyyy-MM-dd HH:mm}");
            await writer.WriteLineAsync("xmin,xmax,ymin,ymax,radius,xspacing,yspacing,xnum,ynum");
            await writer.WriteLineAsync(FormattableString.Invariant($"{XMin:F2},{XMax:F2},{YMin:F2},{YMax:F2},{Radius:F2},{XSpacing:F2},{YSpacing:F2},{NumX},{NumY}"));

            string[] values = new string[NumX];
            int i = 0;
            for (int y = 0; y < NumY; y++)
            {
                for (int x = 0; x < NumX; x++)
                {
                    values[x] = float.IsNaN(ZCoordinates[i]) ? FormattableString.Invariant($"{0,7}") : FormattableString.Invariant(($"{ZCoordinates[i],7:F3}"));
                    i++;
                }
                await writer.WriteLineAsync(string.Join(',', values));
            }
        }
    }
}
