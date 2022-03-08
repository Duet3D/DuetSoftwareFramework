using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.Files;
using System;
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
        public static async Task<Message> Process(Commands.Code code)
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
                                    return new Message(MessageType.Error, "Pause the print before attempting to cancel it");
                                }

                                // Reassign the code's cancellation token to ensure M0/M1 is forwarded to RRF
                                if (code.Channel == CodeChannel.File)
                                {
                                    code.ResetCancellationToken();
                                }

                                // Invalidate the print file and make sure no more codes are read from it
                                await FileExecution.Job.CancelAsync();
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
                            return new Message(MessageType.Success, json);
                        }
                        if (sParam == 3)
                        {
                            string json = FileLists.GetFileList(virtualDirectory, physicalDirectory, startAt, maxSize);
                            return new Message(MessageType.Success, json);
                        }

                        // Print standard G-code response
                        Compatibility compatibility;
                        using (await Model.Provider.AccessReadOnlyAsync())
                        {
                            compatibility = Model.Provider.Get.Inputs[code.Channel].Compatibility;
                        }

                        StringBuilder result = new();
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

                        return new Message(MessageType.Success, result.ToString());
                    }
                    throw new OperationCanceledException();

                // Initialize SD card
                case 21:
                    if (await SPI.Interface.Flush(code))
                    {
                        if (code.Parameter('P', 0) == 0)
                        {
                            // M21 (P0) will always work because it's always mounted
                            return new Message();
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
                            return new Message(MessageType.Error, "Filename expected");
                        }

                        string physicalFile = await FilePath.ToPhysicalAsync(file, FileDirectory.GCodes);
                        if (!File.Exists(physicalFile))
                        {
                            return new Message(MessageType.Error, $"Could not find file {file}");
                        }

                        using (await FileExecution.Job.LockAsync())
                        {
                            if (code.Channel != CodeChannel.File && FileExecution.Job.IsProcessing)
                            {
                                return new Message(MessageType.Error, "Cannot set file to print, because a file is already being printed");
                            }
                            await FileExecution.Job.SelectFile(physicalFile);
                        }

                        // Let RRF do everything else
                        break;
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
                                return new Message(MessageType.Error, "Cannot print, because no file is selected!");
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
                                return new Message(MessageType.Error, "Not printing a file");
                            }

                            CodeParameter sParam = code.Parameter('S');
                            if (sParam != null)
                            {
                                if (sParam < 0L || sParam > FileExecution.Job.FileLength)
                                {
                                    return new Message(MessageType.Error, "Position is out of range");
                                }
                                await FileExecution.Job.SetFilePosition(sParam);
                            }
                        }

                        // P parameter is handled by RRF if present
                        break;
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
                                return new Message(MessageType.Success, $"SD printing byte {filePosition}/{FileExecution.Job.FileLength}");
                            }
                            return new Message(MessageType.Success, "Not SD printing.");
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
                                return new Message(MessageType.Error, "Another file is already being written to");
                            }

                            string file = code.GetUnprecedentedString();
                            if (string.IsNullOrWhiteSpace(file))
                            {
                                return new Message(MessageType.Error, "Filename expected");
                            }

                            string prefix = (await code.EmulatingMarlin()) ? "ok\n" : string.Empty;
                            string physicalFile = await FilePath.ToPhysicalAsync(file, FileDirectory.GCodes);
                            try
                            {
                                FileStream fileStream = new(physicalFile, FileMode.Create, FileAccess.Write);
                                StreamWriter writer = new(fileStream);
                                Commands.Code.FilesBeingWritten[numChannel] = writer;
                                return new Message(MessageType.Success, prefix + $"Writing to file: {file}");
                            }
                            catch (Exception e)
                            {
                                _logger.Debug(e, "Failed to open file for writing");
                                return new Message(MessageType.Error, prefix + $"Can't open file {file} for writing.");
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
                                await Commands.Code.FilesBeingWritten[numChannel].DisposeAsync();
                                Commands.Code.FilesBeingWritten[numChannel] = null;
                                await stream.DisposeAsync();

                                if (await code.EmulatingMarlin())
                                {
                                    return new Message(MessageType.Success, "Done saving file.");
                                }
                                return new Message();
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
                            return new Message(MessageType.Error, $"Failed to delete file {file}: {e.Message}");
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
                            try
                            {
                                // Get fileinfo
                                if (code.MinorNumber != 1)
                                {
                                    string file = await FilePath.ToPhysicalAsync(code.GetUnprecedentedString(), FileDirectory.GCodes);
                                    GCodeFileInfo info = await InfoParser.Parse(file, false);

                                    string json = JsonSerializer.Serialize(info, JsonHelper.DefaultJsonOptions);
                                    return new Message(MessageType.Success, "{\"err\":0," + json[1..]);
                                }

                                // Get thumbnail
                                string pParam = code.Parameter('P');
                                if (pParam == null)
                                {
                                    return new Message(MessageType.Error, "Missing parameter 'P'");
                                }
                                if (code.Parameter('S') == null)
                                {
                                    return new Message(MessageType.Error, "Missing parameter 'S'");
                                }

                                string filename = await FilePath.ToPhysicalAsync(pParam, FileDirectory.GCodes);
                                string thumbnailJson = await InfoParser.ParseThumbnail(filename, code.Parameter('S'));
                                return new Message(MessageType.Success, thumbnailJson);
                            }
                            catch (Exception e)
                            {
                                _logger.Debug(e, "Failed to return file information");
                                return new Message(MessageType.Success, "{\"err\":1}");
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
                                return new Message(MessageType.Error, "Filename expected");
                            }

                            string physicalFile = await FilePath.ToPhysicalAsync(file, FileDirectory.GCodes);
                            if (!File.Exists(physicalFile))
                            {
                                return new Message(MessageType.Error, $"GCode file \"{file}\" not found");
                            }

                            using (await FileExecution.Job.LockAsync())
                            {
                                if (code.Channel != CodeChannel.File && FileExecution.Job.IsProcessing)
                                {
                                    return new Message(MessageType.Error, "Cannot set file to simulate, because a file is already being printed");
                                }

                                await FileExecution.Job.SelectFile(physicalFile, true);
                                // Simulation is started when M37 has been processed by the firmware
                            }
                        }

                        // Let RRF do everything else
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
                            await using FileStream stream = new(physicalFile, FileMode.Open, FileAccess.Read);

                            using System.Security.Cryptography.SHA1 sha1 = System.Security.Cryptography.SHA1.Create();
                            byte[] hash = await Task.Run(() => sha1.ComputeHash(stream), code.CancellationToken);

                            return new Message(MessageType.Success, BitConverter.ToString(hash).Replace("-", string.Empty));
                        }
                        catch (Exception e)
                        {
                            _logger.Debug(e, "Failed to compute SHA1 checksum");
                            if (e is AggregateException ae)
                            {
                                e = ae.InnerException;
                            }
                            return new Message(MessageType.Error, $"Could not compute SHA1 checksum for file {file}: {e.Message}");
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
                                    return new Message(MessageType.Success, $"{{\"SDinfo\":{{\"slot\":{index},present:0}}}}");
                                }

                                Volume storage = Model.Provider.Get.Volumes[index];
                                var output = new
                                {
                                    SDinfo = new
                                    {
                                        slot = index,
                                        present = 1,
                                        capacity = storage.Capacity,
                                        partitionSize = storage.PartitionSize,
                                        free = storage.FreeSpace,
                                        speed = storage.Speed
                                    }
                                };
                                return new Message(MessageType.Success, JsonSerializer.Serialize(output, JsonHelper.DefaultJsonOptions));
                            }
                            else
                            {
                                if (index < 0 || index >= Model.Provider.Get.Volumes.Count)
                                {
                                    return new Message(MessageType.Error, $"Bad SD slot number: {index}");
                                }

                                Volume storage = Model.Provider.Get.Volumes[index];
                                return new Message(MessageType.Success, $"SD card in slot {index}: capacity {storage.Capacity / (1000 * 1000 * 1000):F2}Gb, partition size {storage.PartitionSize / (1000 * 1000 * 1000):F2}Gb,free space {storage.FreeSpace / (1000 * 1000 * 1000):F2}Gb, speed {storage.Speed / (1000 * 1000):F2}MBytes/sec");
                            }
                        }
                    }
                    throw new OperationCanceledException();

                // Flag current macro file as (not) pausable
                case 98:
                    if (code.Parameter('R') != null)
                    {
                        if (await SPI.Interface.Flush(code))
                        {
                            await SPI.Interface.SetMacroPausable(code.Channel, code.Parameter('R') != 0);
                        }
                        else
                        {
                            throw new OperationCanceledException();
                        }
                    }
                    break;

                // Emergency Stop
                case 112:
                    if (code.Flags.HasFlag(CodeFlags.IsPrioritized) || await SPI.Interface.Flush(code))
                    {
                        await SPI.Interface.EmergencyStop();
                        using (await Model.Provider.AccessReadWriteAsync())
                        {
                            Model.Provider.Get.State.Status = MachineStatus.Halted;
                        }
                        return new Message();
                    }
                    throw new OperationCanceledException();

                // Immediate DSF diagnostics
                case 122:
                    if (code.Parameter('B', 0) == 0 && code.GetUnprecedentedString() == "DSF")
                    {
                        Message result = new();
                        await Diagnostics(result);
                        return result;
                    }
                    break;

                // Create Directory on SD-Card
                case 470:
                    if (await SPI.Interface.Flush(code))
                    {
                        string path = code.Parameter('P');
                        if (path == null)
                        {
                            return new Message(MessageType.Error, "Missing directory name");
                        }
                        string physicalPath = await FilePath.ToPhysicalAsync(path);

                        try
                        {
                            Directory.CreateDirectory(physicalPath);
                        }
                        catch (Exception e)
                        {
                            _logger.Debug(e, "Failed to create directory");
                            return new Message(MessageType.Error, $"Failed to create directory {path}: {e.Message}");
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
                            return new Message(MessageType.Error, $"Failed to rename file or directory {from} to {to}: {e.Message}");
                        }
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
                            return new Message(MessageType.Success, content);
                        }

                        string configFileFallback = await FilePath.ToPhysicalAsync(FilePath.ConfigFileFallback, FileDirectory.System);
                        if (File.Exists(configFileFallback))
                        {
                            string content = await File.ReadAllTextAsync(configFileFallback);
                            return new Message(MessageType.Success, content);
                        }
                        return new Message(MessageType.Error, "Configuration file not found");
                    }
                    throw new OperationCanceledException();

                // Set configuration file folder
                case 505:
                    if (await SPI.Interface.Flush(code))
                    {
                        string directory = code.Parameter('P');
                        if (!string.IsNullOrEmpty(directory))
                        {
                            await using (await SPI.Interface.LockMovementAndWaitForStandstill(code.Channel))
                            {
                                string physicalDirectory = await FilePath.ToPhysicalAsync(directory, "sys");
                                if (Directory.Exists(physicalDirectory))
                                {
                                    string virtualDirectory = await FilePath.ToVirtualAsync(physicalDirectory);
                                    using (await Model.Provider.AccessReadWriteAsync())
                                    {
                                        Model.Provider.Get.Directories.System = virtualDirectory;
                                    }
                                    return new Message();
                                }
                            }
                            return new Message(MessageType.Error, "Directory not found");
                        }

                        using (await Model.Provider.AccessReadOnlyAsync())
                        {
                            return new Message(MessageType.Success, $"Sys file path is {Model.Provider.Get.Directories.System}");
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
                                return new Message(MessageType.Error, "Machine name is too long");
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
                                return new Message(MessageType.Error, "Machine name must consist of the same letters and digits as configured by the Linux hostname");
                            }

                            // Hostname is legit - pass this code on to RRF so it can update the name too
                        }
                        break;
                    }
                    throw new OperationCanceledException();

                // Set Password
                case 551:
                    if (await SPI.Interface.Flush(code))
                    {
                        string password = code.Parameter('P');
                        if (password != null)
                        {
                            using (await Model.Provider.AccessReadWriteAsync())
                            {
                                Model.Provider.Password = password;
                            }
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
                            return new Message();
                        }

                        using (await Model.Provider.AccessReadOnlyAsync())
                        {
                            if (string.IsNullOrEmpty(Model.Provider.Get.Network.CorsSite))
                            {
                                return new Message(MessageType.Success, "CORS disabled");
                            }
                            return new Message(MessageType.Success, $"CORS enabled for site '{Model.Provider.Get.Network.CorsSite}'");
                        }
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
                                await System.Diagnostics.Process.Start("timedatectl", $"set-time {date:yyyy-MM-dd}").WaitForExitAsync(Program.CancellationToken);
                                seen = true;
                            }
                            else
                            {
                                return new Message(MessageType.Error, "Invalid date format");
                            }
                        }

                        CodeParameter sParam = code.Parameter('S');
                        if (sParam != null)
                        {
                            if (DateTime.TryParseExact(sParam, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime time))
                            {
                                await System.Diagnostics.Process.Start("timedatectl", $"set-time {time:HH:mm:ss}").WaitForExitAsync(Program.CancellationToken);
                                seen = true;
                            }
                            else
                            {
                                return new Message(MessageType.Error, "Invalid time format");
                            }
                        }

                        CodeParameter tParam = code.Parameter('T');
                        if (tParam != null)
                        {
                            if (File.Exists($"/usr/share/zoneinfo/{tParam}"))
                            {
                                await System.Diagnostics.Process.Start("timedatectl", $"set-timezone ${tParam}").WaitForExitAsync(Program.CancellationToken);
                                seen = true;
                            }
                            else
                            {
                                return new Message(MessageType.Error, "Invalid time zone");
                            }
                        }

                        if (!seen)
                        {
                            return new Message(MessageType.Success, $"Current date and time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
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
                                    return new Message(MessageType.Success, "Event logging is disabled");
                                }
                                return new Message(MessageType.Success, $"Event logging is enabled at log level {Model.Provider.Get.State.LogLevel.ToString().ToLowerInvariant()}");
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
                                return new Message(MessageType.Error, "Missing filename in M929 command");
                            }

                            await Utility.Logger.StartAsync(logFile, logLevel);
                        }
                        else
                        {
                            await Utility.Logger.StopAsync();
                        }
                        return new Message();
                    }
                    throw new OperationCanceledException();

                // Update the firmware
                case 997:
                    if (((int[])code.Parameter('S', new[] { 0 })).Contains(0) && code.Parameter('B', 0) == 0)
                    {
                        if (await SPI.Interface.Flush(code))
                        {
                            // Get the IAP and Firmware files
                            string iapFile, firmwareFile;
                            using (await Model.Provider.AccessReadOnlyAsync())
                            {
                                if (Model.Provider.Get.Boards.Count == 0)
                                {
                                    return new Message(MessageType.Error, "No boards have been detected");
                                }

                                // There are now two different IAP binaries, check which one to use
                                iapFile = Model.Provider.Get.Boards[0].IapFileNameSBC;
                                firmwareFile = code.Parameter('P') ?? Model.Provider.Get.Boards[0].FirmwareFileName;
                            }

                            if (string.IsNullOrEmpty(iapFile) || string.IsNullOrEmpty(firmwareFile))
                            {
                                return new Message(MessageType.Error, "Cannot update firmware because IAP and firmware filenames are unknown");
                            }

                            string physicalIapFile = await FilePath.ToPhysicalAsync(iapFile, FileDirectory.Firmware);
                            if (!File.Exists(physicalIapFile))
                            {
                                string fallbackIapFile = await FilePath.ToPhysicalAsync($"0:/firmware/{iapFile}");
                                if (!File.Exists(fallbackIapFile))
                                {
                                    fallbackIapFile = await FilePath.ToPhysicalAsync(iapFile, FileDirectory.System);
                                    if (!File.Exists(fallbackIapFile))
                                    {
                                        return new Message(MessageType.Error, $"Failed to find IAP file {iapFile}");
                                    }
                                }
                                _logger.Warn("Using fallback IAP file {0}", fallbackIapFile);
                                physicalIapFile = fallbackIapFile;
                            }

                            string physicalFirmwareFile = await FilePath.ToPhysicalAsync(firmwareFile, FileDirectory.Firmware);
                            if (!File.Exists(physicalFirmwareFile))
                            {
                                string fallbackFirmwareFile = await FilePath.ToPhysicalAsync($"0:/firmware/{firmwareFile}");
                                if (!File.Exists(fallbackFirmwareFile))
                                {
                                    fallbackFirmwareFile = await FilePath.ToPhysicalAsync(firmwareFile, FileDirectory.System);
                                    if (!File.Exists(fallbackFirmwareFile))
                                    {
                                        return new Message(MessageType.Error, $"Failed to find firmware file {firmwareFile}");
                                    }
                                }
                                _logger.Warn("Using fallback firmware file {0}", fallbackFirmwareFile);
                                physicalFirmwareFile = fallbackFirmwareFile;
                            }

                            // Stop all the plugins
                            Commands.StopPlugins stopCommand = new();
                            await stopCommand.Execute();

                            // Flash the firmware
                            await using FileStream iapStream = new(physicalIapFile, FileMode.Open, FileAccess.Read);
                            await using FileStream firmwareStream = new(physicalFirmwareFile, FileMode.Open, FileAccess.Read);
                            if (Path.GetExtension(firmwareFile) == ".uf2")
                            {
                                await using MemoryStream unpackedFirmwareStream = await Utility.Firmware.UnpackUF2(firmwareStream);
                                await SPI.Interface.UpdateFirmware(iapStream, unpackedFirmwareStream);
                            }
                            else
                            {
                                await SPI.Interface.UpdateFirmware(iapStream, firmwareStream);
                            }

                            // Terminate the program - or - restart the plugins when done
                            if (Settings.UpdateOnly || !Settings.NoTerminateOnReset)
                            {
                                _ = code.CodeTask.ContinueWith(async task =>
                                {
                                    await task;
                                    await Program.Shutdown();
                                }, TaskContinuationOptions.RunContinuationsAsynchronously);
                            }
                            else
                            {
                                await Model.Updater.WaitForFullUpdate();

                                Commands.StartPlugins startCommand = new();
                                await startCommand.Execute();
                            }
                            return new Message();
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
                            await SPI.Interface.ResetFirmware();
                            return new Message();
                        }
                        throw new OperationCanceledException();
                    }
                    break;
            }
            return null;
        }

        /// <summary>
        /// React to an executed M-code before its result is returned
        /// </summary>
        /// <param name="code">Code processed by RepRapFirmware</param>
        /// <returns>Result to output</returns>
        /// <remarks>This method shall be used only to update values that are time-critical. Others are supposed to be updated via the object model</remarks>
        public static async Task CodeExecuted(Commands.Code code)
        {
            if (code.Result == null || code.Result.Type != MessageType.Success)
            {
                return;
            }

            switch (code.MajorNumber)
            {
                // Stop or Unconditional stop
                // Sleep or Conditional stop
                // Resume print
                // Select file and start SD print
                // Simulate file
                case 0:
                case 1:
                case 24:
                case 32:
                case 37:
                    using (await FileExecution.Job.LockAsync())
                    {
                        // Start sending file instructions to RepRapFirmware or finish the cancellation process
                        FileExecution.Job.Resume();
                    }
                    break;

                // Diagnostics
                case 122:
                    if (code.Parameter('B', 0) == 0 && code.Parameter('P', 0) == 0 && code.GetUnprecedentedString() != "DSF" && !string.IsNullOrEmpty(code.Result.Content))
                    {
                        await Diagnostics(code.Result);
                    }
                    break;

                // Reset controller
                case 999:
                    if (!Settings.NoTerminateOnReset && code.Parameters.Count == 0)
                    {
                        // DCS is supposed to terminate via M999 unless this option is explicitly disabled
                        _ = code.CodeTask.ContinueWith(async task =>
                        {
                            await task;
                            await Program.Shutdown();
                        }, TaskContinuationOptions.RunContinuationsAsynchronously);
                    }
                    break;
            }
        }

        /// <summary>
        /// Print the diagnostics
        /// </summary>
        /// <param name="result">Target to write to</param>
        /// <returns>Asynchronous task</returns>
        private static async Task Diagnostics(Message result)
        {
            StringBuilder builder = new();
            builder.AppendLine("=== Duet Control Server ===");
            builder.AppendLine($"Duet Control Server v{Program.Version}");

            await FileExecution.Job.Diagnostics(builder);
            await Model.Updater.Diagnostics(builder);
            await SPI.Interface.Diagnostics(builder);

            result.Append(MessageType.Success, builder.ToString());
        }
    }
}
