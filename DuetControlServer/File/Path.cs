using System.IO;

namespace DuetControlServer
{
    public static partial class File
    {
        // Resolve a RepRapFirmware/FatFS-style file path to an actual Linux file path.
        // In the future this may be expanded to allow virtual disks
        public static string ResolvePath(string filePath)
        {
            if (filePath.StartsWith("0:/"))
            {
                filePath = filePath.Substring(3);
            }
            return Path.Combine(Settings.BaseDirectory, filePath);
        }
    }
}
