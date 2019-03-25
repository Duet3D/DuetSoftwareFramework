using System.IO;
using System.Text.RegularExpressions;
using DuetControlServer.SPI;

namespace DuetControlServer
{
    public static partial class File
    {
        /// <summary>
        /// Resolve a RepRapFirmware/FatFs-style file path to an actual UNIX file path.
        /// The first drive (0:/) is reserved for usage with the base directory as
        /// specified in the settings.
        /// </summary>
        /// <param name="filePath">File path to resolve</param>
        /// <returns>Resolved file path</returns>
        public static string ResolvePath(string filePath)
        {
            Match match = Regex.Match(filePath, "^(\\d+):?/?(.*)");
            if (match.Success)
            {
                int driveNumber = int.Parse(match.Groups[1].Value);
                if (driveNumber == 0)
                {
                    return Path.Combine(Settings.BaseDirectory, match.Groups[2].Value);
                }
                if (driveNumber > 0 && driveNumber < ModelProvider.Current.Storages.Count)
                {
                    return Path.Combine(ModelProvider.Current.Storages[driveNumber].Path, match.Groups[2].Value);
                }
            }

            return filePath;
        }
    }
}
