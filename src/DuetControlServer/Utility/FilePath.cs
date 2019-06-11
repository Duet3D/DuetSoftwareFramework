using DuetAPI.Machine;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DuetControlServer
{
    /// <summary>
    /// Static class used to provide functions for file path resolution
    /// </summary>
    public static class FilePath
    {
        /// <summary>
        /// Resolve a RepRapFirmware/FatFs-style file path to a physical file path.
        /// The first drive (0:/) is reserved for usage with the base directory as specified in the settings.
        /// </summary>
        /// <param name="filePath">File path to resolve</param>
        /// <param name="directory">Directory of the file path if none is specified</param>
        /// <returns>Resolved file path</returns>
        public static async Task<string> ToPhysical(string filePath, string directory = null)
        {
            Match match = Regex.Match(filePath, "^(\\d+):?/?(.*)");
            if (match.Success)
            {
                int driveNumber = int.Parse(match.Groups[1].Value);
                if (driveNumber == 0)
                {
                    return Path.Combine(Path.GetFullPath(Settings.BaseDirectory), match.Groups[2].Value);
                }

                using (await Model.Provider.AccessReadOnlyAsync())
                {
                    MachineModel model = Model.Provider.Get;
                    if (driveNumber > 0 && driveNumber < model.Storages.Count)
                    {
                        return Path.Combine(model.Storages[driveNumber].Path, match.Groups[2].Value);
                    }
                }
            }

            if (directory != null && !filePath.StartsWith('/'))
            {
                return Path.Combine(Path.GetFullPath(Settings.BaseDirectory), directory, filePath);
            }
            return Path.Combine(Path.GetFullPath(Settings.BaseDirectory), filePath.StartsWith('/') ? filePath.Substring(1) : filePath);
        }

        /// <summary>
        /// Convert a physical ile path to a RRF-style file path.
        /// The first drive (0:/) is reserved for usage with the base directory as specified in the settings.
        /// </summary>
        /// <param name="filePath">File path to convert</param>
        /// <returns>Resolved file path</returns>
        public static async Task<string> ToVirtual(string filePath)
        {
            if (filePath.StartsWith(Settings.BaseDirectory))
            {
                filePath = filePath.Substring(Settings.BaseDirectory.EndsWith('/') ? Settings.BaseDirectory.Length : (Settings.BaseDirectory.Length + 1));
                return Path.Combine("0:/", filePath);
            }

            using (await Model.Provider.AccessReadOnlyAsync())
            {
                foreach (var storage in Model.Provider.Get.Storages)
                {
                    if (filePath.StartsWith(storage.Path))
                    {
                        return Path.Combine("0:/", filePath.Substring(storage.Path.Length));
                    }
                }
            }

            return Path.Combine("0:/", filePath);
        }
    }
}
