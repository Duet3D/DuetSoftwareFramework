using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.Files;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DuetControlServer.Codes
{
    /// <summary>
    /// Static class that processes M-codes in the control server
    /// </summary>
    public static class MCodes
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Process an M-code that should be interpreted by the control server
        /// </summary>
        /// <param name="code">Code to process</param>
        /// <returns>Result of the code if the code completed, else null</returns>
        public static async Task<CodeResult> Process(Commands.Code code)
        {
            if (code.Channel == CodeChannel.File && FileExecution.Job.IsSimulating)
            {
                // Ignore M-codes from files in simulation mode...
                return null;
            }

            switch (code.MajorNumber)
            {
                // Stop or Unconditional stop
                // Sleep or Conditional stop
                case 0:
                case 1:
                    if (await SPI.Interface.Flush(code))
                    {
                        using (await FileExecution.Job.LockAsync())
                        {
                            if (FileExecution.Job.IsFileSelected)
                            {
                                // M0/M1 may be used in a print file to terminate it
                                if (code.Channel != CodeChannel.File && !FileExecution.Job.IsPaused)
                                {
                                    return new CodeResult(MessageType.Error, "Pause the print before attempting to cancel it");
                                }
                            }
                        }
                        break;
                    }
                    throw new OperationCanceledException();

                // List SD card
                case 20:
                    if (await SPI.Interface.Flush(code))
                    {
                        // Resolve the directory
                        string virtualDirectory = code.Parameter('P');
                        if (virtualDirectory == null)
                        {
                            using (await Model.Provider.AccessReadOnlyAsync())
                            {
                                virtualDirectory = Model.Provider.Get.Directories.GCodes;
                            }
                        }
                        string physicalDirectory = await FilePath.ToPhysicalAsync(virtualDirectory);

                        // Make sure to stay within limits if it is a request from the firmware
                        int maxSize = -1;
                        if (code.Flags.HasFlag(CodeFlags.IsFromFirmware))
                        {
                            maxSize = Settings.MaxMessageLength;
                        }

                        // Check if JSON file lists were requested
                        int startAt = Math.Max(code.Parameter('R') ?? 0, 0);
                        CodeParameter sParam = code.Parameter('S', 0);
                        if (sParam == 2)
                        {
                            string json = FileLists.GetFiles(virtualDirectory, physicalDirectory, startAt, true, maxSize);
                            return new CodeResult(MessageType.Success, json);
                        }
                        if (sParam == 3)
                        {
                            string json = FileLists.GetFileList(virtualDirectory, physicalDirectory, startAt, maxSize);
                            return new CodeResult(MessageType.Success, json);
                        }

                        // Print standard G-code response
                        Compatibility compatibility;
                        using (await Model.Provider.AccessReadOnlyAsync())
                        {
                            compatibility = Model.Provider.Get.Inputs[code.Channel].Compatibility;
                        }

                        StringBuilder result = new StringBuilder();
                        if (compatibility == Compatibility.Default || compatibility == Compatibility.RepRapFirmware)
                        {
                            result.AppendLine("GCode files:");
                        }
                        else if (compatibility == Compatibility.Marlin || compatibility == Compatibility.NanoDLP)
                        {
                            result.AppendLine("Begin file list:");
                        }

                        int numItems = 0;
                        bool itemFound = false;
                        foreach (string file in Directory.EnumerateFileSystemEntries(physicalDirectory))
                        {
                            if (numItems++ >= startAt)
                            {
                                string filename = Path.GetFileName(file);
                                if (compatibility == Compatibility.Marlin || compatibility == Compatibility.NanoDLP)
                                {
                                    result.AppendLine(filename);
                                }
                                else
                                {
                                    if (itemFound)
                                    {
                                        result.Append(',');
                                    }
                                    result.Append($"\"{filename}\"");
                                }
                                itemFound = true;
                            }
                        }

                        if (compatibility == Compatibility.Marlin || compatibility == Compatibility.NanoDLP)
                        {
                            if (!itemFound)
                            {
                                result.AppendLine("NONE");
                            }
                            result.Append("End file list");
                        }

                        return new CodeResult(MessageType.Success, result.ToString());
                    }
                    throw new OperationCanceledException();

                // Initialize SD card
                case 21:
                    if (await SPI.Interface.Flush(code))
                    {
                        if (code.Parameter('P', 0) == 0)
                        {
                            // M21 (P0) will always work because it's always mounted
                            return new CodeResult();
                        }
                        throw new NotSupportedException();
                    }
                    throw new OperationCanceledException();

                // Release SD card
                case 22:
                    throw new NotSupportedException();

                // Select a file to print
                case 23:
                case 32:
                    if (await SPI.Interface.Flush(code))
                    {
                        string file = code.GetUnprecedentedString();
                        if (string.IsNullOrWhiteSpace(file))
                        {
                            return new CodeResult(MessageType.Error, "Filename expected");
                        }

                        string physicalFile = await FilePath.ToPhysicalAsync(file, FileDirectory.GCodes);
                        if (!File.Exists(physicalFile))
                        {
                            return new CodeResult(MessageType.Error, $"Could not find file {file}");
                        }

                        using (await FileExecution.Job.LockAsync())
                        {
                            if (code.Channel != CodeChannel.File && FileExecution.Job.IsProcessing)
                            {
                                return new CodeResult(MessageType.Error, "Cannot set file to print, because a file is already being printed");
                            }
                            await FileExecution.Job.SelectFile(physicalFile);
                        }

                        if (await code.EmulatingMarlin())
                        {
                            return new CodeResult(MessageType.Success, "File opened\nFile selected");
                        }
                        return new CodeResult(MessageType.Success, $"File {file} selected for printing");
                    }
                    throw new OperationCanceledException();


                // Resume a file print
                case 24:
                    if (await SPI.Interface.Flush(code))
                    {
                        using (await FileExecution.Job.LockAsync())
                        {
                            if (!FileExecution.Job.IsFileSelected)
                            {
                                return new CodeResult(MessageType.Error, "Cannot print, because no file is selected!");
                            }
                        }

                        // Let RepRapFirmware process this request so it can invoke resume.g. When M24 completes, the file is resumed
                        break;
                    }
                    throw new OperationCanceledException();

                // Set SD position
                case 26:
                    if (await SPI.Interface.Flush(code))
                    {
                        using (await FileExecution.Job.LockAsync())
                        {
                            if (!FileExecution.Job.IsFileSelected)
                            {
                                return new CodeResult(MessageType.Error, "Not printing a file");
                            }

                            CodeParameter sParam = code.Parameter('S');
                            if (sParam != null)
                            {
                                if (sParam < 0L || sParam > FileExecution.Job.FileLength)
                                {
                                    return new CodeResult(MessageType.Error, "Position is out of range");
                                }
                                await FileExecution.Job.SetFilePosition(sParam);
                            }
                        }

                        // P is not supported yet

                        return new CodeResult();
                    }
                    throw new OperationCanceledException();

                // Report SD print status
                case 27:
                    if (await SPI.Interface.Flush(code))
                    {
                        using (await FileExecution.Job.LockAsync())
                        {
                            if (FileExecution.Job.IsFileSelected)
                            {
                                long filePosition = await FileExecution.Job.GetFilePosition();
                                return new CodeResult(MessageType.Success, $"SD printing byte {filePosition}/{FileExecution.Job.FileLength}");
                            }
                            return new CodeResult(MessageType.Success, "Not SD printing.");
                        }
                    }
                    throw new OperationCanceledException();

                // Begin write to SD card
                case 28:
                    if (await SPI.Interface.Flush(code))
                    {
                        int numChannel = (int)code.Channel;
                        using (await Commands.Code.FileLocks[numChannel].LockAsync(Program.CancellationToken))
                        {
                            if (Commands.Code.FilesBeingWritten[numChannel] != null)
                            {
                                return new CodeResult(MessageType.Error, "Another file is already being written to");
                            }

                            string file = code.GetUnprecedentedString();
                            if (string.IsNullOrWhiteSpace(file))
                            {
                                return new CodeResult(MessageType.Error, "Filename expected");
                            }

                            string prefix = (await code.EmulatingMarlin()) ? "ok\n" : string.Empty;
                            string physicalFile = await FilePath.ToPhysicalAsync(file, FileDirectory.GCodes);
                            try
                            {
                                FileStream fileStream = new FileStream(physicalFile, FileMode.Create, FileAccess.Write);
                                StreamWriter writer = new StreamWriter(fileStream);
                                Commands.Code.FilesBeingWritten[numChannel] = writer;
                                return new CodeResult(MessageType.Success, prefix + $"Writing to file: {file}");
                            }
                            catch (Exception e)
                            {
                                _logger.Debug(e, "Failed to open file for writing");
                                return new CodeResult(MessageType.Error, prefix + $"Can't open file {file} for writing.");
                            }
                        }
                    }
                    throw new OperationCanceledException();

                // End write to SD card
                case 29:
                    if (await SPI.Interface.Flush(code))
                    {
                        int numChannel = (int)code.Channel;
                        using (await Commands.Code.FileLocks[numChannel].LockAsync(Program.CancellationToken))
                        {
                            if (Commands.Code.FilesBeingWritten[numChannel] != null)
                            {
                                Stream stream = Commands.Code.FilesBeingWritten[numChannel].BaseStream;
                                Commands.Code.FilesBeingWritten[numChannel].Dispose();
                                Commands.Code.FilesBeingWritten[numChannel] = null;
                                stream.Dispose();

                                if (await code.EmulatingMarlin())
                                {
                                    return new CodeResult(MessageType.Success, "Done saving file.");
                                }
                                return new CodeResult();
                            }
                            break;
                        }
                    }
                    throw new OperationCanceledException();

                // Delete a file on the SD card
                case 30:
                    if (await SPI.Interface.Flush(code))
                    {
                        string file = code.GetUnprecedentedString();
                        string physicalFile = await FilePath.ToPhysicalAsync(file);

                        try
                        {
                            File.Delete(physicalFile);
                        }
                        catch (Exception e)
                        {
                            _logger.Debug(e, "Failed to delete file");
                            return new CodeResult(MessageType.Error, $"Failed to delete file {file}: {e.Message}");
                        }
                    }
                    throw new OperationCanceledException();

                // For case 32, see case 23

                // Return file information
                case 36:
                    if (code.Parameters.Count > 0)
                    {
                        if (await SPI.Interface.Flush(code))
                        {
                            string file = await FilePath.ToPhysicalAsync(code.GetUnprecedentedString(), FileDirectory.GCodes);
                            try
                            {
                                ParsedFileInfo info = await InfoParser.Parse(file);

                                string json = JsonSerializer.Serialize(info, JsonHelper.DefaultJsonOptions);
                                return new CodeResult(MessageType.Success, "{\"err\":0," + json[1..]);
                            }
                            catch (Exception e)
                            {
                                _logger.Debug(e, "Failed to return file information");
                                return new CodeResult(MessageType.Success, "{\"err\":1}");
                            }
                        }
                        throw new OperationCanceledException();
                    }
                    break;

                // Simulate file
                case 37:
                    if (await SPI.Interface.Flush(code))
                    {
                        CodeParameter pParam = code.Parameter('P');
                        if (pParam != null)
                        {
                            string file = pParam;
                            if (string.IsNullOrWhiteSpace(file))
                            {
                                return new CodeResult(MessageType.Error, "Filename expected");
                            }

                            string physicalFile = await FilePath.ToPhysicalAsync(file, FileDirectory.GCodes);
                            if (!File.Exists(physicalFile))
                            {
                                return new CodeResult(MessageType.Error, $"GCode file \"{file}\" not found\n");
                            }

                            using (await FileExecution.Job.LockAsync())
                            {
                                if (code.Channel != CodeChannel.File && FileExecution.Job.IsProcessing)
                                {
                                    return new CodeResult(MessageType.Error, "Cannot set file to simulate, because a file is already being printed");
                                }

                                await FileExecution.Job.SelectFile(physicalFile, true);
                                // Simulation is started when M37 has been processed by the firmware
                            }
                        }
                        break;
                    }
                    throw new OperationCanceledException();

                // Compute SHA1 hash of target file
                case 38:
                    if (await SPI.Interface.Flush(code))
                    {
                        string file = code.GetUnprecedentedString();
                        string physicalFile = await FilePath.ToPhysicalAsync(file);

                        try
                        {
                            using FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read);

                            byte[] hash;
                            using System.Security.Cryptography.SHA1 sha1 = System.Security.Cryptography.SHA1.Create();
                            hash = await Task.Run(() => sha1.ComputeHash(stream), code.CancellationToken);

                            return new CodeResult(MessageType.Success, BitConverter.ToString(hash).Replace("-", string.Empty));
                        }
                        catch (Exception e)
                        {
                            _logger.Debug(e, "Failed to compute SHA1 checksum");
                            if (e is AggregateException ae)
                            {
                                e = ae.InnerException;
                            }
                            return new CodeResult(MessageType.Error, $"Could not compute SHA1 checksum for file {file}: {e.Message}");
                        }
                    }
                    throw new OperationCanceledException();

                // Report SD card information
                case 39:
                    if (await SPI.Interface.Flush(code))
                    {
                        using (await Model.Provider.AccessReadOnlyAsync())
                        {
                            int index = code.Parameter('P', 0);
                            if (code.Parameter('S', 0) == 2)
                            {
                                if (index < 0 || index >= Model.Provider.Get.Volumes.Count)
                                {
                                    return new CodeResult(MessageType.Success, $"{{\"SDinfo\":{{\"slot\":{index},present:0}}}}");
                                }

                                Volume storage = Model.Provider.Get.Volumes[index];
                                var output = new
                                {
                                    SDinfo = new
                                    {
                                        slot = index,
                                        present = 1,
                                        capacity = storage.Capacity,
                                        free = storage.FreeSpace,
                                        speed = storage.Speed
                                    }
                                };
                                return new CodeResult(MessageType.Success, JsonSerializer.Serialize(output, JsonHelper.DefaultJsonOptions));
                            }
                            else
                            {
                                if (index < 0 || index >= Model.Provider.Get.Volumes.Count)
                                {
                                    return new CodeResult(MessageType.Error, $"Bad SD slot number: {index}");
                                }

                                Volume storage = Model.Provider.Get.Volumes[index];
                                return new CodeResult(MessageType.Success, $"SD card in slot {index}: capacity {storage.Capacity / (1000 * 1000 * 1000):F2}Gb, free space {storage.FreeSpace / (1000 * 1000 * 1000):F2}Gb, speed {storage.Speed / (1000 * 1000):F2}MBytes/sec");
                            }
                        }
                    }
                    throw new OperationCanceledException();

                // Emergency Stop
                case 112:
                    if (code.Flags.HasFlag(CodeFlags.IsPrioritized) || await SPI.Interface.Flush(code))
                    {
                        await SPI.Interface.EmergencyStop();
                        using (await Model.Provider.AccessReadWriteAsync())
                        {
                            Model.Provider.Get.State.Status = MachineStatus.Halted;
                        }
                        return new CodeResult();
                    }
                    throw new OperationCanceledException();

                // Immediate DSF diagnostics
                case 122:
                    if (code.Parameter('B', 0) == 0 && code.GetUnprecedentedString() == "DSF")
                    {
                        CodeResult result = new CodeResult();
                        await Diagnostics(result);
                        return result;
                    }
                    break;

                // Save heightmap
                case 374:
                    if (await SPI.Interface.Flush(code))
                    {
                        string file = code.Parameter('P', FilePath.DefaultHeightmapFile);
                        string physicalFile = await FilePath.ToPhysicalAsync(file, FileDirectory.System);

                        try
                        {
                            Heightmap map;
                            await using (await SPI.Interface.LockMovementAndWaitForStandstill(code.Channel))
                            {
                                map = await SPI.Interface.GetHeightmap();
                            }

                            if (map.NumX * map.NumY > 0)
                            {
                                await map.Save(physicalFile);

                                string virtualFile = await FilePath.ToVirtualAsync(physicalFile);
                                using (await Model.Provider.AccessReadWriteAsync())
                                {
                                    Model.Provider.Get.Move.Compensation.File = virtualFile;
                                }
                                return new CodeResult(MessageType.Success, $"Height map saved to file {file}");
                            }
                            return new CodeResult();
                        }
                        catch (Exception e)
                        {
                            _logger.Debug(e, "Failed to save height map");
                            if (e is AggregateException ae)
                            {
                                e = ae.InnerException;
                            }
                            return new CodeResult(MessageType.Error, $"Failed to save height map to file {file}: {e.Message}");
                        }
                    }
                    throw new OperationCanceledException();

                // Load heightmap
                case 375:
                    if (await SPI.Interface.Flush(code))
                    {
                        string file = code.Parameter('P', FilePath.DefaultHeightmapFile);
                        string physicalFile = await FilePath.ToPhysicalAsync(file, FileDirectory.System);

                        try
                        {
                            Heightmap map = new Heightmap();
                            await map.Load(physicalFile);

                            await using (await SPI.Interface.LockMovementAndWaitForStandstill(code.Channel))
                            {
                                await SPI.Interface.SetHeightmap(map);
                            }

                            string virtualFile = await FilePath.ToVirtualAsync(physicalFile);
                            using (await Model.Provider.AccessReadWriteAsync())
                            {
                                Model.Provider.Get.Move.Compensation.File = virtualFile;
                            }

                            CodeResult result = new CodeResult();
                            using (await Model.Provider.AccessReadOnlyAsync())
                            {
                                if (Model.Provider.Get.Move.Axes.Any(axis => axis.Letter == 'Z' && !axis.Homed))
                                {
                                    result.Add(MessageType.Warning, "The height map was loaded when the current Z=0 datum was not determined. This may result in a height offset.");
                                }
                            }
                            result.Add(MessageType.Success, $"Height map loaded from file {file}");
                            return result;
                        }
                        catch (Exception e)
                        {
                            _logger.Debug(e, "Failed to load height map");
                            if (e is AggregateException ae)
                            {
                                e = ae.InnerException;
                            }
                            return new CodeResult(MessageType.Error, $"Failed to load height map from file {file}: {e.Message}");
                        }
                    }
                    throw new OperationCanceledException();

                // Create Directory on SD-Card
                case 470:
                    if (await SPI.Interface.Flush(code))
                    {
                        string path = code.Parameter('P');
                        if (path == null)
                        {
                            return new CodeResult(MessageType.Error, "Missing directory name");
                        }
                        string physicalPath = await FilePath.ToPhysicalAsync(path);

                        try
                        {
                            Directory.CreateDirectory(physicalPath);
                        }
                        catch (Exception e)
                        {
                            _logger.Debug(e, "Failed to create directory");
                            return new CodeResult(MessageType.Error, $"Failed to create directory {path}: {e.Message}");
                        }
                    }
                    throw new OperationCanceledException();

                // Rename File/Directory on SD-Card
                case 471:
                    if (await SPI.Interface.Flush(code))
                    {
                        string from = code.Parameter('S');
                        string to = code.Parameter('T');

                        try
                        {
                            string source = await FilePath.ToPhysicalAsync(from);
                            string destination = await FilePath.ToPhysicalAsync(to);

                            if (File.Exists(source))
                            {
                                if (File.Exists(destination) && code.Parameter('D', false))
                                {
                                    File.Delete(destination);
                                }
                                File.Move(source, destination);
                            }
                            else if (Directory.Exists(source))
                            {
                                if (Directory.Exists(destination) && code.Parameter('D', false))
                                {
                                    // This could be recursive but at the moment we mimic RRF's behaviour
                                    Directory.Delete(destination);
                                }
                                Directory.Move(source, destination);
                            }
                            throw new FileNotFoundException();
                        }
                        catch (Exception e)
                        {
                            _logger.Debug(e, "Failed to rename file or directory");
                            return new CodeResult(MessageType.Error, $"Failed to rename file or directory {from} to {to}: {e.Message}");
                        }
                    }
                    throw new OperationCanceledException();

                // Store parameters
                case 500:
                    if (await SPI.Interface.Flush(code))
                    {
                        await Model.Updater.WaitForFullUpdate(Program.CancellationToken);
                        await ConfigOverride.Save(code);
                        return new CodeResult();
                    }
                    throw new OperationCanceledException();

                // Print settings
                case 503:
                    if (await SPI.Interface.Flush(code))
                    {
                        string configFile = await FilePath.ToPhysicalAsync(FilePath.ConfigFile, FileDirectory.System);
                        if (File.Exists(configFile))
                        {
                            string content = await File.ReadAllTextAsync(configFile);
                            return new CodeResult(MessageType.Success, content);
                        }

                        string configFileFallback = await FilePath.ToPhysicalAsync(FilePath.ConfigFileFallback, FileDirectory.System);
                        if (File.Exists(configFileFallback))
                        {
                            string content = await File.ReadAllTextAsync(configFileFallback);
                            return new CodeResult(MessageType.Success, content);
                        }
                        return new CodeResult(MessageType.Error, "Configuration file not found");
                    }
                    throw new OperationCanceledException();

                // Set configuration file folder
                case 505:
                    if (await SPI.Interface.Flush(code))
                    {
                        string directory = code.Parameter('P');
                        if (!string.IsNullOrEmpty(directory))
                        {
                            string physicalDirectory = await FilePath.ToPhysicalAsync(directory, "sys");
                            if (Directory.Exists(physicalDirectory))
                            {
                                string virtualDirectory = await FilePath.ToVirtualAsync(physicalDirectory);
                                using (await Model.Provider.AccessReadWriteAsync())
                                {
                                    Model.Provider.Get.Directories.System = virtualDirectory;
                                }
                                return new CodeResult();
                            }
                            return new CodeResult(MessageType.Error, "Directory not found");
                        }

                        using (await Model.Provider.AccessReadOnlyAsync())
                        {
                            return new CodeResult(MessageType.Success, $"Sys file path is {Model.Provider.Get.Directories.System}");
                        }
                    }
                    throw new OperationCanceledException();

                // Set Name
                case 550:
                    if (await SPI.Interface.Flush(code))
                    {
                        // Verify the P parameter
                        string pParam = code.Parameter('P');
                        if (!string.IsNullOrEmpty(pParam))
                        {
                            if (pParam.Length > 40)
                            {
                                return new CodeResult(MessageType.Error, "Machine name is too long");
                            }

                            // Strip letters and digits from the machine name
                            string machineName = string.Empty;
                            foreach (char c in Environment.MachineName)
                            {
                                if (char.IsLetterOrDigit(c))
                                {
                                    machineName += c;
                                }
                            }

                            // Strip letters and digits from the desired name
                            string desiredName = string.Empty;
                            foreach (char c in pParam)
                            {
                                if (char.IsLetterOrDigit(c))
                                {
                                    desiredName += c;
                                }
                            }

                            // Make sure the subset of letters and digits is equal
                            if (!machineName.Equals(desiredName, StringComparison.CurrentCultureIgnoreCase))
                            {
                                return new CodeResult(MessageType.Error, "Machine name must consist of the same letters and digits as configured by the Linux hostname");
                            }

                            // Hostname is legit - pretend we didn't see this code so RRF can interpret it
                        }
                        break;
                    }
                    throw new OperationCanceledException();

                // Configure network protocols
                case 586:
                    if (await SPI.Interface.Flush(code))
                    {
                        string corsSite = code.Parameter('C');
                        if (corsSite != null)
                        {
                            using (await Model.Provider.AccessReadWriteAsync())
                            {
                                Model.Provider.Get.Network.CorsSite = string.IsNullOrWhiteSpace(corsSite) ? null : corsSite;
                            }
                            return new CodeResult();
                        }

                        using (await Model.Provider.AccessReadOnlyAsync())
                        {
                            if (string.IsNullOrEmpty(Model.Provider.Get.Network.CorsSite))
                            {
                                return new CodeResult(MessageType.Success, "CORS disabled");
                            }
                            return new CodeResult(MessageType.Success, $"CORS enabled for site '{Model.Provider.Get.Network.CorsSite}'");
                        }
                    }
                    throw new OperationCanceledException();

                // Configure filament
                case 703:
                    if (await SPI.Interface.Flush(code))
                    {
                        await Model.Updater.WaitForFullUpdate(Program.CancellationToken);
                        break;
                    }
                    throw new OperationCanceledException();

                // Set current RTC date and time
                case 905:
                    if (await SPI.Interface.Flush(code))
                    {
                        bool seen = false;

                        CodeParameter pParam = code.Parameter('P');
                        if (pParam != null)
                        {
                            if (DateTime.TryParseExact(pParam, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
                            {
                                System.Diagnostics.Process.Start("timedatectl", $"set-time {date:yyyy-MM-dd}").WaitForExit();
                                seen = true;
                            }
                            else
                            {
                                return new CodeResult(MessageType.Error, "Invalid date format");
                            }
                        }

                        CodeParameter sParam = code.Parameter('S');
                        if (sParam != null)
                        {
                            if (DateTime.TryParseExact(sParam, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime time))
                            {
                                System.Diagnostics.Process.Start("timedatectl", $"set-time {time:HH:mm:ss}").WaitForExit();
                                seen = true;
                            }
                            else
                            {
                                return new CodeResult(MessageType.Error, "Invalid time format");
                            }
                        }

                        if (!seen)
                        {
                            return new CodeResult(MessageType.Success, $"Current date and time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                        }
                    }
                    throw new OperationCanceledException();

                // Start/stop event logging to SD card
                case 929:
                    if (await SPI.Interface.Flush(code))
                    {
                        CodeParameter sParam = code.Parameter('S');
                        if (sParam == null)
                        {
                            using (await Model.Provider.AccessReadOnlyAsync())
                            {
                                if (Model.Provider.Get.State.LogLevel == LogLevel.Off)
                                {
                                    return new CodeResult(MessageType.Success, "Event logging is disabled");
                                }
                                return new CodeResult(MessageType.Success, $"Event logging is enabled at log level {Model.Provider.Get.State.LogLevel.ToString().ToLowerInvariant()}");
                            }
                        }

                        if (sParam > 0 && sParam < 4)
                        {
                            LogLevel logLevel = (int)sParam switch
                            {
                                1 => LogLevel.Warn,
                                2 => LogLevel.Info,
                                3 => LogLevel.Debug,
                                _ => LogLevel.Off
                            };

                            string defaultLogFile = Utility.Logger.DefaultLogFile;
                            using (await Model.Provider.AccessReadOnlyAsync())
                            {
                                if (!string.IsNullOrEmpty(Model.Provider.Get.State.LogFile))
                                {
                                    defaultLogFile = Model.Provider.Get.State.LogFile;
                                }
                            }
                            string logFile = code.Parameter('P', defaultLogFile);
                            if (string.IsNullOrWhiteSpace(logFile))
                            {
                                return new CodeResult(MessageType.Error, "Missing filename in M929 command");
                            }

                            await Utility.Logger.Start(logFile, logLevel);
                        }
                        else
                        {
                            await Utility.Logger.Stop();
                        }
                        return new CodeResult();
                    }
                    throw new OperationCanceledException();

                // Update the firmware
                case 997:
                    if (((int[])code.Parameter('S', new int[] { 0 })).Contains(0) && code.Parameter('B', 0) == 0)
                    {
                        if (await SPI.Interface.Flush(code))
                        {
                            string iapFile, firmwareFile;
                            using (await Model.Provider.AccessReadOnlyAsync())
                            {
                                if (Model.Provider.Get.Boards.Count == 0)
                                {
                                    return new CodeResult(MessageType.Error, "No boards have been detected");
                                }

                                // There are now two different IAP binaries, check which one to use
                                iapFile = Model.Provider.Get.Boards[0].IapFileNameSBC;
                                firmwareFile = Model.Provider.Get.Boards[0].FirmwareFileName;
                            }

                            if (string.IsNullOrEmpty(iapFile) || string.IsNullOrEmpty(firmwareFile))
                            {
                                return new CodeResult(MessageType.Error, "Cannot update firmware because IAP and firmware filenames are unknown");
                            }

                            iapFile = await FilePath.ToPhysicalAsync(iapFile, FileDirectory.Firmware);
                            if (!File.Exists(iapFile))
                            {
                                return new CodeResult(MessageType.Error, $"Failed to find IAP file {iapFile}");
                            }

                            firmwareFile = await FilePath.ToPhysicalAsync(firmwareFile, FileDirectory.Firmware);
                            if (!File.Exists(firmwareFile))
                            {
                                return new CodeResult(MessageType.Error, $"Failed to find firmware file {firmwareFile}");
                            }

                            IEnumerable<string> stoppedPlugins = await Utility.Plugins.StopPlugins();

                            using FileStream iapStream = new FileStream(iapFile, FileMode.Open, FileAccess.Read);
                            using FileStream firmwareStream = new FileStream(firmwareFile, FileMode.Open, FileAccess.Read);
                            if (Path.GetExtension(firmwareFile) == ".uf2")
                            {
                                using MemoryStream unpackedFirmwareStream = await Utility.UF2.Unpack(firmwareStream);
                                await SPI.Interface.UpdateFirmware(iapStream, unpackedFirmwareStream);
                            }
                            else
                            {
                                await SPI.Interface.UpdateFirmware(iapStream, firmwareStream);
                            }

                            if (Settings.UpdateOnly || !Settings.NoTerminateOnReset)
                            {
                                Program.CancelSource.Cancel();
                            }
                            else
                            {
                                await Model.Updater.WaitForFullUpdate(Program.CancellationToken);
                                await Utility.Plugins.StartPlugins(stoppedPlugins);
                            }
                            return new CodeResult();
                        }
                        throw new OperationCanceledException();
                    }
                    break;

                // Request resend of line
                case 998:
                    throw new NotSupportedException();

                // Reset controller
                case 999:
                    if (code.Parameters.Count == 0)
                    {
                        if (code.Flags.HasFlag(CodeFlags.IsPrioritized) || await SPI.Interface.Flush(code))
                        {
                            await SPI.Interface.Reset();
                            return new CodeResult();
                        }
                        throw new OperationCanceledException();
                    }
                    break;
            }
            return null;
        }

        /// <summary>
        /// React to an executed M-code before its result is returend
        /// </summary>
        /// <param name="code">Code processed by RepRapFirmware</param>
        /// <returns>Result to output</returns>
        /// <remarks>This method shall be used only to update values that are time-critical. Others are supposed to be updated via the object model</remarks>
        public static async Task CodeExecuted(Code code)
        {
            if (!code.Result.IsSuccessful)
            {
                return;
            }

            switch (code.MajorNumber)
            {
                // Stop or Unconditional stop
                // Sleep or Conditional stop
                case 0:
                case 1:
                    using (await FileExecution.Job.LockAsync())
                    {
                        if (FileExecution.Job.IsFileSelected)
                        {
                            // Invalidate the print file and make sure no more codes are read from it
                            await FileExecution.Job.Cancel();
                        }
                    }
                    break;

                // Resume print
                // Select file and start SD print
                // Simulate file
                case 24:
                case 32:
                case 37:
                    using (await FileExecution.Job.LockAsync())
                    {
                        // Start sending file instructions to RepRapFirmware
                        FileExecution.Job.Resume();
                    }
                    break;

                // Diagnostics
                case 122:
                    if (code.Parameter('B', 0) == 0 && code.GetUnprecedentedString() != "DSF" && !code.Result.IsEmpty)
                    {
                        await Diagnostics(code.Result);
                    }
                    break;

                // Set compatibility
                case 555:
                    if (code.Parameter('P') != null)
                    {
                        Compatibility compatibility = (Compatibility)(int)code.Parameter('P');
                        using (await Model.Provider.AccessReadWriteAsync())
                        {
                            Model.Provider.Get.Inputs[code.Channel].Compatibility = compatibility;
                        }
                    }
                    break;

                // Reset controller
                case 999:
                    if (!Settings.NoTerminateOnReset && code.Parameters.Count == 0)
                    {
                        // DCS is supposed to terminate via M999 unless this option is explicitly disabled
                        _ = Task.Run(Program.Shutdown);
                    }
                    break;
            }
        }

        /// <summary>
        /// Print the diagnostics
        /// </summary>
        /// <param name="result">Target to write to</param>
        /// <returns>Asynchronous task</returns>
        private static async Task Diagnostics(CodeResult result)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("=== Duet Control Server ===");
            builder.AppendLine($"Duet Control Server v{Program.Version}");

            await SPI.Interface.Diagnostics(builder);
            SPI.DataTransfer.Diagnostics(builder);
            await FileExecution.Job.Diagnostics(builder);

            result.Add(MessageType.Success, builder.ToString().TrimEnd());
        }
    }
}
