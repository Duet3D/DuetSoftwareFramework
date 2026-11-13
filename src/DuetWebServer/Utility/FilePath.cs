using DuetAPIClient;
using System;
using System.IO;
using System.Threading.Tasks;

namespace DuetWebServer.Utility
{
    /// <summary>
    /// Static class providing helper functions for file paths
    /// </summary>
    public static class FilePath
    {
        /// <summary>
        /// Check if a path refers to the root directory of a volume
        /// </summary>
        /// <param name="connection">Connection to resolve the volume roots with</param>
        /// <param name="path">RepRapFirmware-style path as passed by the client</param>
        /// <param name="resolvedPath">Physical path it was resolved to</param>
        /// <returns>Whether the resolved path is the base directory or the root directory of the requested volume</returns>
        public static async Task<bool> IsVolumeRootAsync(CommandConnection connection, string path, string resolvedPath)
        {
            resolvedPath = NormalizePath(resolvedPath);
            if (resolvedPath == NormalizePath(await connection.ResolvePath("0:/")))
            {
                return true;
            }

            int colonIndex = path.IndexOf(':');
            return colonIndex > 0 && path.AsSpan(0, colonIndex).IndexOfAnyExceptInRange('0', '9') < 0 && resolvedPath == NormalizePath(await connection.ResolvePath(path[..(colonIndex + 1)] + '/'));
        }

        /// <summary>
        /// Resolve relative segments of a physical path and strip the trailing directory separator
        /// </summary>
        /// <param name="physicalPath">Physical path to normalize</param>
        /// <returns>Normalized path</returns>
        private static string NormalizePath(string physicalPath) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(physicalPath));
    }
}
