using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DuetAPI.Utility
{
    /// <summary>
    /// Helper class to generate RRF-style file lists
    /// </summary>
    public static class FileLists
    {
        /// <summary>
        /// Make a filelist container for M20
        /// </summary>
        /// <param name="items">Items to include</param>
        /// <param name="directory">RRF directory</param>
        /// <param name="startAt">First item</param>
        /// <returns>JSON file list object</returns>
        private static string MakeFileListContainer(object items, string directory, int startAt)
        {
            Dictionary<string, object> jsonContainer = new Dictionary<string, object>
            {
                ["dir"] = directory,
                ["first"] = Math.Max(startAt, 0),
                ["files"] = items,
                ["next"] = 0,
                ["err"] = 0
            };
            return JsonSerializer.Serialize(jsonContainer, JsonHelper.DefaultJsonOptions);
        }

        /// <summary>
        /// Get a /rr_files or M20 files response
        /// </summary>
        /// <param name="directory">RRF path to the directory</param>
        /// <param name="physicalDirectory">Physical directory</param>
        /// <param name="startAt">First item to send</param>
        /// <param name="flagDirs">Prefix directories with an asterisk</param>
        /// <returns>JSON file list</returns>
        public static string GetFiles(string directory, string physicalDirectory, int startAt = 0, bool flagDirs = false)
        {
            List<string> files = new List<string>();

            try
            {
                int numItems = 0;

                // List directories
                foreach (string dir in Directory.EnumerateDirectories(physicalDirectory))
                {
                    if (startAt < 0 || numItems++ >= startAt)
                    {
                        string name = Path.GetFileName(dir);
                        files.Add(flagDirs ? "*" + name : name);
                    }
                }

                // List files
                foreach (string file in Directory.EnumerateFiles(physicalDirectory))
                {
                    if (startAt < 0 || numItems++ >= startAt)
                    {
                        string name = Path.GetFileName(file);
                        files.Add(name);
                    }
                }

                // Return items encapsulated in a body
                return MakeFileListContainer(files, directory, Math.Min(startAt, numItems));
            }
            catch
            {
                if (startAt < 0)
                {
                    // Something went wrong...
                    return "{\"err\":2}";
                }
                throw;
            }
        }

        /// <summary>
        /// Get a /rr_filelist or M20 files response
        /// </summary>
        /// <param name="directory">RRF path to the directory</param>
        /// <param name="physicalDirectory">Physical directory</param>
        /// <param name="startAt">First file index to return. Set startAt to -1 to omit error handling and the JSON object container</param>
        /// <returns>JSON list</returns>
        public static string GetFileList(string directory, string physicalDirectory, int startAt = -1)
        {
            List<object> fileList = new List<object>();

            try
            {
                int numItems = 0;

                // List directories
                foreach (string dir in Directory.EnumerateDirectories(physicalDirectory))
                {
                    if (startAt < 0 || numItems++ >= startAt)
                    {
                        DirectoryInfo info = new DirectoryInfo(dir);
                        fileList.Add(new { type = 'd', name = info.Name, date = info.LastWriteTime });
                    }
                }

                // List files
                foreach (string file in Directory.EnumerateFiles(physicalDirectory))
                {
                    if (startAt < 0 || numItems++ >= startAt)
                    {
                        FileInfo info = new FileInfo(file);
                        fileList.Add(new { type = 'f', name = info.Name, size = info.Length, date = info.LastWriteTime });
                    }
                }


                if (startAt >= 0)
                {
                    // Return items encapsulated in a body
                    return MakeFileListContainer(fileList, directory, Math.Min(startAt, numItems));
                }
            }
            catch
            {
                if (startAt < 0)
                {
                    // Something went wrong...
                    return "{\"err\":2}";
                }
                throw;
            }

            // Return items without body
            return JsonSerializer.Serialize(fileList, JsonHelper.DefaultJsonOptions);
        }
    }
}
