using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Machine;
using DuetAPI.Utility;
using DuetControlServer.FileExecution;
using Newtonsoft.Json;
using Nito.AsyncEx;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace DuetControlServer.Codes
{
    /// <summary>
    /// Static class that processes M-codes in the control server
    /// </summary>
    public static class MCodes
    {
        private static readonly AsyncLock _fileToPrintLock = new AsyncLock();
        private static string _fileToPrint;

        /// <summary>
        /// Process an M-code that should be interpreted by the control server
        /// </summary>
        /// <param name="code">Code to process</param>
        /// <returns>Result of the code if the code completed, else null</returns>
        public static async Task<CodeResult> Process(Commands.Code code)
        {
            switch (code.MajorNumber)
            {
                // Cancel print
                case 0:
                case 1:
                    if (await SPI.Interface.Flush(code.Channel) && Print.IsPrinting)
                    {
                        // Invalidate the print file to make sure no more codes are read from the file
                        await Print.Cancel();
                    }
                    break;

                // Select a file to print
                case 23:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        string file = await FilePath.ToPhysicalAsync(code.GetUnprecedentedString(), "gcodes");
                        if (File.Exists(file))
                        {
                            using (await _fileToPrintLock.LockAsync())
                            {
                                _fileToPrint = file;
                            }
                            return new CodeResult(MessageType.Success, $"File {code.GetUnprecedentedString()} selected for printing");
                        }
                        return new CodeResult(MessageType.Error, $"Could not find file {code.GetUnprecedentedString()}");
                    }
                    return new CodeResult();

                // Resume a file print
                case 24:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        // See if a file print is supposed to be started
                        if (!Print.IsPaused)
                        {
                            string file;
                            using (await _fileToPrintLock.LockAsync())
                            {
                                file = string.Copy(_fileToPrint);
                            }

                            if (string.IsNullOrEmpty(file))
                            {
                                return new CodeResult(MessageType.Error, "Cannot print, because no file is selected!");
                            }

                            return await Print.Start(file, code.Channel);
                        }

                        // Let RepRapFirmware process this request so it can invoke resume.g
                        return null;
                    }
                    return new CodeResult();

                // Pause print
                case 25:
                case 226:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        if (Print.IsPrinting && !Print.IsPaused)
                        {
                            // Stop reading any more codes from the file being printed. Everything else is handled by RRF
                            await Print.Pause();
                        }
                        return null;
                    }
                    return new CodeResult();

                // Set SD position
                case 26:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        CodeParameter sParam = code.Parameter('S');
                        if (sParam != null)
                        {
                            Print.Position = sParam;
                        }

                        // P is not supported yet
                    }
                    return new CodeResult();

                // Report SD print status
                case 27:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        if (Print.IsPrinting)
                        {
                            return new CodeResult(MessageType.Success, $"SD printing byte {Print.Position}/{Print.Length}");
                        }
                        return new CodeResult(MessageType.Success, "Not SD printing.");
                    }
                    return new CodeResult();

                // Delete a file on the SD card
                case 30:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        string file = code.GetUnprecedentedString();
                        try
                        {
                            File.Delete(await FilePath.ToPhysicalAsync(file));
                        }
                        catch (Exception e)
                        {
                            return new CodeResult(MessageType.Error, $"Failed to delete file {file}: {e.Message}");
                        }
                    }
                    return new CodeResult();

                // Start a file print
                case 32:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        string file = await FilePath.ToPhysicalAsync(code.GetUnprecedentedString(), "gcodes");
                        if (File.Exists(file))
                        {
                            using (await _fileToPrintLock.LockAsync())
                            {
                                _fileToPrint = file;
                            }
                            return await Print.Start(file, code.Channel);
                        }
                        return new CodeResult(MessageType.Error, $"Could not find file {code.GetUnprecedentedString()}");
                    }
                    return new CodeResult();

                // Return file information
                case 36:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        if (code.Parameters.Count > 0)
                        {
                            try
                            {
                                string file = await FilePath.ToPhysicalAsync(code.GetUnprecedentedString());
                                ParsedFileInfo info = await FileInfoParser.Parse(file);

                                string json = JsonConvert.SerializeObject(info, JsonHelper.DefaultSettings);
                                return new CodeResult(MessageType.Success, "{\"err\":0," + json.Substring(1));
                            }
                            catch
                            {
                                return new CodeResult(MessageType.Success, "{\"err\":1}");
                            }
                        }
                    }
                    return new CodeResult();

                // Simulate file
                case 37:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        // TODO: Check if file exists
                        // TODO: Execute and await pseudo-M37 with IsPreProcessed = true so the firmware enters the right simulation state
                        // TODO: Start file print
                        return new CodeResult(MessageType.Warning, "M37 is not supported yet");
                    }
                    return new CodeResult();

                // Compute SHA1 hash of target file
                case 38:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        string file = await FilePath.ToPhysicalAsync(code.GetUnprecedentedString());
                        try
                        {
                            using (FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read))
                            {
                                byte[] hash;
                                using (var sha1 = System.Security.Cryptography.SHA1.Create())
                                {
                                    hash = await Task.Run(() => sha1.ComputeHash(stream), Program.CancelSource.Token);
                                }
                                return new CodeResult(MessageType.Success, BitConverter.ToString(hash).Replace("-", ""));
                            }
                        }
                        catch (AggregateException ae)
                        {
                            return new CodeResult(MessageType.Error, $"Could not compute SHA1 checksum for file {file}: {ae.InnerException.Message}");
                        }
                    }
                    return new CodeResult();

                // Report SD card information
                case 39:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        using (await Model.Provider.AccessReadOnlyAsync())
                        {
                            int index = code.Parameter('P', 0);
                            if (code.Parameter('S', 0) == 2)
                            {
                                if (index < 0 || index >= Model.Provider.Get.Storages.Count)
                                {
                                    return new CodeResult(MessageType.Success, $"{{\"SDinfo\":{{\"slot\":{index},present:0}}}}");
                                }

                                Storage storage = Model.Provider.Get.Storages[index];
                                var output = new
                                {
                                    SDinfo = new
                                    {
                                        slot = index,
                                        present = 1,
                                        capacity = storage.Capacity,
                                        free = storage.Free,
                                        speed = storage.Speed
                                    }
                                };
                                return new CodeResult(MessageType.Success, JsonConvert.SerializeObject(output));
                            }
                            else
                            {
                                if (index < 0 || index >= Model.Provider.Get.Storages.Count)
                                {
                                    return new CodeResult(MessageType.Error, $"Bad SD slot number: {index}");
                                }

                                Storage storage = Model.Provider.Get.Storages[index];
                                return new CodeResult(MessageType.Success, $"SD card in slot {index}: capacity {storage.Capacity / (1000 * 1000 * 1000):F2}Gb, free space {storage.Free / (1000 * 1000 * 1000):F2}Gb, speed {storage.Speed / (1000 * 1000):F2}MBytes/sec");
                            }
                        }
                    }
                    return new CodeResult();

                // Emergency Stop - unconditional and interpreteted immediately when read
                case 112:
                    await SPI.Interface.RequestEmergencyStop();
                    using (await Model.Provider.AccessReadWriteAsync())
                    {
                        Model.Provider.Get.State.Status = MachineStatus.Halted;
                    }
                    return new CodeResult();

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
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        string file = code.Parameter('P', FilePath.DefaultHeightmapFile);
                        try
                        {
                            if (await SPI.Interface.LockMovementAndWaitForStandstill(code.Channel))
                            {
                                Heightmap map = await SPI.Interface.GetHeightmap();
                                await SPI.Interface.UnlockAll(code.Channel);

                                await map.Save(await FilePath.ToPhysicalAsync(file, "sys"));
                                return new CodeResult(MessageType.Success, $"Height map saved to file {file}");
                            }
                        }
                        catch (AggregateException ae)
                        {
                            return new CodeResult(MessageType.Error, $"Failed to save height map to file {file}: {ae.InnerException.Message}");
                        }
                    }
                    return new CodeResult();

                // Load heightmap
                case 375:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        string file = await FilePath.ToPhysicalAsync(code.Parameter('P', FilePath.DefaultHeightmapFile), "sys");
                        try
                        {
                            Heightmap map = new Heightmap();
                            await map.Load(file);

                            if (await SPI.Interface.LockMovementAndWaitForStandstill(code.Channel))
                            {
                                await SPI.Interface.SetHeightmap(map);
                                await SPI.Interface.UnlockAll(code.Channel);
                            }
                        }
                        catch (AggregateException ae)
                        {
                            return new CodeResult(MessageType.Error, $"Failed to load height map from file {file}: {ae.InnerException.Message}");
                        }
                    }
                    return new CodeResult();

                // Create Directory on SD-Card
                case 470:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        string path = code.Parameter('P', "");
                        try
                        {
                            Directory.CreateDirectory(await FilePath.ToPhysicalAsync(path));
                        }
                        catch (Exception e)
                        {
                            return new CodeResult(MessageType.Error, $"Failed to create directory {path}: {e.Message}");
                        }
                    }
                    return new CodeResult();

                // Rename File/Directory on SD-Card
                case 471:
                    if (await SPI.Interface.Flush(code.Channel))
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
                            return new CodeResult(MessageType.Error, $"Failed to rename file or directory {from} to {to}: {e.Message}");
                        }
                    }
                    return new CodeResult();

                // Store parameters
                case 500:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        await Utility.ConfigOverride.Save(code);
                    }
                    return new CodeResult();

                // Print settings
                case 503:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        string configFile = await FilePath.ToPhysicalAsync(FilePath.ConfigFile, "sys");
                        if (File.Exists(configFile))
                        {
                            string content = await File.ReadAllTextAsync(configFile);
                            return new CodeResult(MessageType.Success, content);
                        }
                        configFile = await FilePath.ToPhysicalAsync(FilePath.ConfigFileFallback, "sys");
                        if (File.Exists(configFile))
                        {
                            string content = await File.ReadAllTextAsync(configFile);
                            return new CodeResult(MessageType.Success, content);
                        }
                        return new CodeResult(MessageType.Error, "Configuration file not found");
                    }
                    return new CodeResult();

                // Set Name
                case 550:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        // Verify the P parameter
                        string pParam = code.Parameter('P');
                        if (pParam.Length > 40)
                        {
                            return new CodeResult(MessageType.Error, "Machine name is too long");
                        }

                        // Strip letters and digits from the machine name
                        string machineName = "";
                        foreach (char c in Environment.MachineName)
                        {
                            if (char.IsLetterOrDigit(c))
                            {
                                machineName += c;
                            }
                        }

                        // Strip letters and digits from the desired name
                        string desiredName = "";
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
                        return null;
                    }
                    return new CodeResult();

                // Set current RTC date and time
                case 905:
                    if (await SPI.Interface.Flush(code.Channel))
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
                    return new CodeResult();

                // Start/stop event logging to SD card
                case 929:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        CodeParameter sParam = code.Parameter('S');
                        if (sParam == null)
                        {
                            using (await Model.Provider.AccessReadOnlyAsync())
                            {
                                return new CodeResult(MessageType.Success, $"Event logging is {(Model.Provider.Get.State.LogFile != null ? "enabled" : "disabled")}");
                            }
                        }

                        if (sParam > 0)
                        {
                            string filename = code.Parameter('P', Utility.Logger.DefaultLogFile);
                            if (string.IsNullOrWhiteSpace(filename))
                            {
                                return new CodeResult(MessageType.Error, "Missing filename in M929 command");
                            }

                            using (await Model.Provider.AccessReadWriteAsync())
                            {
                                string physicalFilename = await FilePath.ToPhysicalAsync(filename, "sys");
                                await Utility.Logger.Start(physicalFilename);

                                Model.Provider.Get.State.LogFile = filename;
                            }

                            return new CodeResult();
                        }

                        using (await Model.Provider.AccessReadWriteAsync())
                        {
                            await Utility.Logger.Stop();
                            Model.Provider.Get.State.LogFile = null;
                        }
                    }
                    return new CodeResult();

                // Update the firmware
                case 997:
                    if (((int[])code.Parameter('S', new int[] { 0 })).Contains(0) && (int)code.Parameter('B', 0) == 0)
                    {
                        if (await SPI.Interface.Flush(code.Channel))
                        {
                            string iapFile, firmwareFile;
                            using (await Model.Provider.AccessReadOnlyAsync())
                            {
                                if (!string.IsNullOrEmpty(Model.Provider.Get.Electronics.ShortName))
                                {
                                    iapFile = await FilePath.ToPhysicalAsync($"Duet3iap_spi_{Model.Provider.Get.Electronics.ShortName}.bin", "sys");
                                    firmwareFile = await FilePath.ToPhysicalAsync($"Duet3Firmware_{Model.Provider.Get.Electronics.ShortName}.bin", "sys");
                                }
                                else
                                {
                                    iapFile = await FilePath.ToPhysicalAsync($"Duet3iap_spi.bin", "sys");
                                    firmwareFile = await FilePath.ToPhysicalAsync("Duet3Firmware.bin", "sys");
                                }
                            }

                            if (!File.Exists(iapFile))
                            {
                                return new CodeResult(MessageType.Error, $"Failed to find IAP file {iapFile}");
                            }

                            if (!File.Exists(firmwareFile))
                            {
                                return new CodeResult(MessageType.Error, $"Failed to find firmware file {firmwareFile}");
                            }

                            FileStream iapStream = new FileStream(iapFile, FileMode.Open, FileAccess.Read);
                            FileStream firmwareStream = new FileStream(firmwareFile, FileMode.Open, FileAccess.Read);
                            await SPI.Interface.UpdateFirmware(iapStream, firmwareStream);
                        }
                        return new CodeResult();
                    }
                    break;

                // Reset controller - unconditional and interpreteted immediately when read
                case 999:
                    if (code.Parameters.Count == 0)
                    {
                        await SPI.Interface.RequestReset();
                        return new CodeResult();
                    }
                    break;
            }
            return null;
        }

        /// <summary>
        /// React to an executed M-code before its result is returend
        /// </summary>
        /// <param name="code">Code processed by RepRapFirmware</param>
        /// <param name="result">Result that it generated</param>
        /// <returns>Result to output</returns>
        /// <remarks>This method shall be used only to update values that are time-critical. Others are supposed to be updated via the object model</remarks>
        public static async Task<CodeResult> CodeExecuted(Code code, CodeResult result)
        {
            if (!result.IsSuccessful)
            {
                return result;
            }

            switch (code.MajorNumber)
            {
                // Resume print
                case 24:
                    if (Print.IsPaused)
                    {
                        // Resume sending file instructions to the firmware
                        Print.Resume();
                    }
                    break;

                // Absolute extrusion
                case 82:
                    using (await Model.Provider.AccessReadWriteAsync())
                    {
                        Model.Provider.Get.Channels[code.Channel].RelativeExtrusion = false;
                    }
                    break;

                // Relative extrusion
                case 83:
                    using (await Model.Provider.AccessReadWriteAsync())
                    {
                        Model.Provider.Get.Channels[code.Channel].RelativeExtrusion = false;
                    }
                    break;

                // Diagnostics
                case 122:
                    if (code.Parameter('B', 0) == 0 && code.GetUnprecedentedString() != "DSF")
                    {
                        await Diagnostics(result);
                    }
                    break;
            }
            return result;
        }

        private static async Task Diagnostics(CodeResult result)
        {
            StringBuilder builder = new StringBuilder();

            builder.AppendLine("=== Duet Control Server ===");
            builder.AppendLine($"Duet Control Server v{Assembly.GetExecutingAssembly().GetName().Version}");
            await SPI.Interface.Diagnostics(builder);
            SPI.DataTransfer.Diagnostics(builder);
            MacroFile.Diagnostics(builder);
            await Print.Diagnostics(builder);

            result.Add(MessageType.Success, builder.ToString().TrimEnd());
        }
    }
}
