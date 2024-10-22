using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DuetAPI.Utility
{
    /// <summary>
    /// Helper class to generate RRF-style file lists
    /// </summary>
    public static class FileLists
    {
        /// <summary>
        /// Get a /rr_files or M20 files response
        /// </summary>
        /// <param name="directory">RRF path to the directory</param>
        /// <param name="physicalDirectory">Physical directory</param>
        /// <param name="startAt">First item to send</param>
        /// <param name="flagDirs">Prefix directories with an asterisk</param>
        /// <param name="maxSize">Maximum size of the file list in bytes or -1 if unset</param>
        /// <param name="maxItems">Maximum number of items to send or -1 if unset</param>
        /// <returns>UTF8-encoded JSON file list</returns>
        public static byte[] GetFilesUtf8(string directory, string physicalDirectory, int startAt = 0, bool flagDirs = false, int maxSize = -1, int maxItems = -1)
        {
            using MemoryStream fileList = new();
            using Utf8JsonWriter writer = new(fileList);

            writer.WriteStartObject();
            writer.WriteString("dir", directory);
            writer.WriteNumber("first", Math.Max(startAt, 0));
            writer.WriteStartArray("files");

            int numItems = 0;
            int GetNextFileListSize(string nextItemToAdd)
            {
                writer.Flush();
                return (int)writer.BytesCommitted + Encoding.UTF8.GetByteCount($@",""{nextItemToAdd}""],\""next\"":{numItems},""err"":0}}");
            }

            try
            {
                // List directories
                foreach (string dir in Directory.EnumerateDirectories(physicalDirectory))
                {
                    if (numItems++ >= startAt)
                    {
                        string name = Path.GetFileName(dir);
                        name = flagDirs ? "*" + name : name;

                        // Check if we're about to exceed the maximum size or max number of items and stop if that is the case
                        if ((maxSize > 0 && GetNextFileListSize(name) > maxSize) || (maxItems > 0 && numItems > Math.Max(startAt, 0) + maxItems))
                        {
                            writer.WriteEndArray();
                            writer.WriteNumber("next", numItems - 1);
                            writer.WriteNumber("err", 0);
                            writer.WriteEndObject();
                            writer.Flush();
                            return fileList.ToArray();
                        }

                        // Write the next item
                        writer.WriteStringValue(name);
                    }
                }

                // List files
                foreach (string file in Directory.EnumerateFiles(physicalDirectory))
                {
                    if (numItems++ >= startAt)
                    {
                        string name = Path.GetFileName(file);

                        // Check if we're about to exceed the maximum size or max number of items and stop if that is the case
                        if ((maxSize > 0 && GetNextFileListSize(name) > maxSize) || (maxItems > 0 && numItems > Math.Max(startAt, 0) + maxItems))
                        {
                            writer.WriteEndArray();
                            writer.WriteNumber("next", numItems - 1);
                            writer.WriteNumber("err", 0);
                            writer.WriteEndObject();
                            writer.Flush();
                            return fileList.ToArray();
                        }

                        // Write the next item
                        writer.WriteStringValue(name);
                    }
                }

                // Return items encapsulated in a body
                writer.WriteEndArray();
                writer.WriteNumber("next", 0);
                writer.WriteNumber("err", 0);
                writer.WriteEndObject();
                writer.Flush();
                return fileList.ToArray();
            }
            catch
            {
                if (startAt >= 0)
                {
                    // Something went wrong...
                    return Encoding.UTF8.GetBytes("{\"err\":2}");
                }
                throw;
            }
        }

        /// <summary>
        /// Get a /rr_files or M20 files response
        /// </summary>
        /// <param name="directory">RRF path to the directory</param>
        /// <param name="physicalDirectory">Physical directory</param>
        /// <param name="startAt">First item to send</param>
        /// <param name="flagDirs">Prefix directories with an asterisk</param>
        /// <param name="maxSize">Maximum size of the file list in bytes or -1 if unset</param>
        /// <param name="maxItems">Maximum number of items to send or -1 if unset</param>
        /// <returns>JSON file list</returns>
        public static string GetFiles(string directory, string physicalDirectory, int startAt = 0, bool flagDirs = false, int maxSize = -1, int maxItems = -1)
        {
            return Encoding.UTF8.GetString(GetFilesUtf8(directory, physicalDirectory, startAt, flagDirs, maxSize, maxItems));
        }

        /// <summary>
        /// Get a /rr_filelist or M20 files response
        /// </summary>
        /// <param name="directory">RRF path to the directory</param>
        /// <param name="physicalDirectory">Physical directory</param>
        /// <param name="startAt">First file index to return. Set startAt to -1 to omit error handling and the JSON object container</param>
        /// <param name="maxSize">Maximum size of the file list in bytes or -1 if unset</param>
        /// <param name="maxItems">Maximum number of items to send or -1 if unset</param>
        /// <returns>UTF8-encoded JSON list</returns>
        public static byte[] GetFileListUtf8(string directory, string physicalDirectory, int startAt = -1, int maxSize = -1, int maxItems = -1)
        {
            using MemoryStream fileList = new();
            using Utf8JsonWriter writer = new(fileList);

            // Write body only if a partial list is requested
            if (startAt >= 0)
            {
                writer.WriteStartObject();
                writer.WriteString("dir", directory);
                writer.WriteNumber("first", Math.Max(startAt, 0));
                writer.WriteStartArray("files");
            }
            else
            {
                writer.WriteStartArray();
            }

            int numItems = 0;
            int GetNextDirectorySize(string nextItemToAdd, DateTime lastWriteTime)
            {
                writer.Flush();
                return (int)writer.BytesCommitted + Encoding.UTF8.GetByteCount($@",{{""type"":""x"",""name"":""{nextItemToAdd}"",""date"":""{lastWriteTime:s}""}}],\""next\"":{numItems},""err"":0}}");
            }
            int GetNextFileSize(string nextItemToAdd, long size, DateTime lastWriteTime)
            {
                writer.Flush();
                return (int)writer.BytesCommitted + Encoding.UTF8.GetByteCount($@",{{""type"":""x"",""name"":""{nextItemToAdd}"",""size"":{size},""date"":""{lastWriteTime:s}""}}],\""next\"":{numItems},""err"":0}}");
            }

            try
            {
                // List directories
                foreach (string dir in Directory.EnumerateDirectories(physicalDirectory))
                {
                    if (numItems++ >= startAt)
                    {
                        DirectoryInfo info = new(dir);

                        // Check if we're about to exceed the maximum size or max number of items and stop if that is the case
                        if ((maxSize > 0 && GetNextDirectorySize(info.Name, info.LastWriteTime) > maxSize) || (maxItems > 0 && numItems > Math.Max(startAt, 0) + maxItems))
                        {
                            if (startAt >= 0)
                            {
                                writer.WriteEndArray();
                                writer.WriteNumber("next", Math.Min(startAt, numItems - 1));
                                writer.WriteNumber("err", 0);
                                writer.WriteEndObject();
                                writer.Flush();
                                return fileList.ToArray();
                            }
                            writer.WriteEndArray();
                            writer.Flush();
                            return fileList.ToArray();
                        }

                        // Write the next item
                        writer.WriteStartObject();
                        writer.WriteString("type", "d");
                        writer.WriteString("name", info.Name);
                        writer.WriteString("date", info.LastWriteTime.ToString("s"));
                        writer.WriteEndObject();
                    }
                }

                // List files
                foreach (string file in Directory.EnumerateFiles(physicalDirectory))
                {
                    if (numItems++ >= startAt)
                    {
                        FileInfo info = new(file);

                        // Check if we're about to exceed the maximum size or max number of items and stop if that is the case
                        if ((maxSize > 0 && GetNextFileSize(info.Name, info.Length, info.LastWriteTime) > maxSize) || (maxItems > 0 && numItems > Math.Max(startAt, 0) + maxItems))
                        {
                            if (startAt >= 0)
                            {
                                writer.WriteEndArray();
                                writer.WriteNumber("next", Math.Min(startAt, numItems - 1));
                                writer.WriteNumber("err", 0);
                                writer.WriteEndObject();
                                writer.Flush();
                                return fileList.ToArray();
                            }
                            writer.WriteEndArray();
                            writer.Flush();
                            return fileList.ToArray();
                        }

                        // Write the next item
                        writer.WriteStartObject();
                        writer.WriteString("type", "f");
                        writer.WriteString("name", info.Name);
                        writer.WriteNumber("size", info.Length);
                        writer.WriteString("date", info.LastWriteTime.ToString("s"));
                        writer.WriteEndObject();
                    }
                }
                writer.WriteEndArray();

                if (startAt >= 0)
                {
                    // Return items encapsulated in a body
                    writer.WriteNumber("next", 0);
                    writer.WriteNumber("err", 0);
                    writer.WriteEndObject();
                    writer.Flush();
                    return fileList.ToArray();
                }
            }
            catch
            {
                if (startAt >= 0)
                {
                    // Something went wrong...
                    return Encoding.UTF8.GetBytes("{\"err\":2}");
                }
                throw;
            }

            // Return items without body
            writer.Flush();
            return fileList.ToArray();
        }

        /// <summary>
        /// Get a /rr_filelist or M20 files response
        /// </summary>
        /// <param name="directory">RRF path to the directory</param>
        /// <param name="physicalDirectory">Physical directory</param>
        /// <param name="startAt">First file index to return. Set startAt to -1 to omit error handling and the JSON object container</param>
        /// <param name="maxSize">Maximum size of the file list in bytes or -1 if unset</param>
        /// <param name="maxItems">Maximum number of items to send or -1 if unset</param>
        /// <returns>JSON list</returns>
        public static string GetFileList(string directory, string physicalDirectory, int startAt = -1, int maxSize = -1, int maxItems = -1)
        {
            return Encoding.UTF8.GetString(GetFileListUtf8(directory, physicalDirectory, startAt, maxSize, maxItems));
        }
    }
}
