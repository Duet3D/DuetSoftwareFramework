using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Machine;
using DuetAPI.Utility;
using DuetControlServer.FileExecution;
using Newtonsoft.Json;
using Nito.AsyncEx;
using System;
using System.IO;
using System.Threading.Tasks;

namespace DuetControlServer.Codes
{
    /// <summary>
    /// Static class that processes M-codes in the control server
    /// </summary>
    public static class MCodes
    {
        private static AsyncLock _fileToPrintLock = new AsyncLock();
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
                    if (Print.IsPrinting)
                    {
                        await Print.Cancel();
                    }
                    break;

                // Select a file to print
                case 23:
                    {
                        string file = await FilePath.ToPhysical(code.GetUnprecedentedString(), "gcodes");
                        if (File.Exists(file))
                        {
                            using (await _fileToPrintLock.LockAsync())
                            {
                                _fileToPrint = file;
                            }
                            return new CodeResult(DuetAPI.MessageType.Success, $"File {file} selected for printing");
                        }
                        return new CodeResult(DuetAPI.MessageType.Error, $"Could not find file {file}");
                    }

                // Resume a file print
                case 24:
                    if (!Print.IsPaused)
                    {
                        string file;
                        using (await _fileToPrintLock.LockAsync())
                        {
                            file = string.Copy(_fileToPrint);
                        }

                        if (string.IsNullOrEmpty(file))
                        {
                            return new CodeResult(DuetAPI.MessageType.Error, "Cannot print, because no file is selected!");
                        }

                        // FIXME Emulate Marlin via "File opened\nFile selected". IMHO this should happen via a CodeChannel property
                        return await Print.Start(file, code.Channel);
                    }
                    break;

                // Pause print
                case 25:
                case 226:
                    if (Print.IsPrinting && !Print.IsPaused)
                    {
                        // Stop sending file instructions to the firmware
                        await Print.Pause();
                    }
                    break;

                // Set SD position
                case 26:
                    {
                        CodeParameter sParam = code.Parameter('S');
                        if (sParam != null)
                        {
                            Print.Position = sParam;
                        }
                        // P is not supported yet
                        return new CodeResult();
                    }

                // Report SD print status
                case 27:
                    if (Print.IsPrinting)
                    {
                        return new CodeResult(DuetAPI.MessageType.Success, $"SD printing byte {Print.Position}/{Print.Length}");
                    }
                    return new CodeResult(DuetAPI.MessageType.Success, "Not SD printing.");

                // Delete a file on the SD card
                case 30:
                    {
                        string file = code.GetUnprecedentedString();
                        try
                        {
                            File.Delete(await FilePath.ToPhysical(file));
                            return new CodeResult();
                        }
                        catch (Exception e)
                        {
                            return new CodeResult(DuetAPI.MessageType.Error, $"Failed to delete file {file}: {e.Message}");
                        }
                    }

                // Start a file print
                case 32:
                    {
                        string file = await FilePath.ToPhysical(code.GetUnprecedentedString());
                        using (await _fileToPrintLock.LockAsync())
                        {
                            _fileToPrint = file;
                        }
                        return await Print.Start(file, code.Channel);
                    }

                // Return file information
                case 36:
                    if (code.Parameters.Count > 0)
                    {
                        try
                        {
                            string file = await FilePath.ToPhysical(code.GetUnprecedentedString());
                            ParsedFileInfo info = await FileInfoParser.Parse(file);

                            string json = JsonConvert.SerializeObject(info, JsonHelper.DefaultSettings);
                            return new CodeResult(MessageType.Success, "{\"err\":0," + json.Substring(1));
                        }
                        catch
                        {
                            return new CodeResult(DuetAPI.MessageType.Success, "{\"err\":1}");
                        }
                    }
                    break;

                // Simulate file
                case 37:
                    // TODO: Check if file exists
                    // TODO: Execute and await pseudo-M37 with IsPreProcessed = true so the firmware enters the right simulation state
                    // TODO: Start file print
                    return new CodeResult(MessageType.Warning, "M37 is not supported yet");

                // Compute SHA1 hash of target file
                case 38:
                    {
                        string file = await FilePath.ToPhysical(code.GetUnprecedentedString());
                        try
                        {
                            using (FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read))
                            {
                                var sha1 = System.Security.Cryptography.SHA1.Create();
                                byte[] hash = await Task.Run(() => sha1.ComputeHash(stream));
                                return new CodeResult(MessageType.Success, BitConverter.ToString(hash).Replace("-", ""));
                            }
                        }
                        catch (AggregateException ae)
                        {
                            return new CodeResult(MessageType.Error, $"Could not compute SHA1 checksum for file {file}: {ae.InnerException.Message}");
                        }
                    }

                // Report SD card information
                case 39:
                    using (await Model.Provider.AccessReadOnly())
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
                            return new CodeResult(MessageType.Success, $"SD card in slot {index}: capacity {storage.Capacity / (1000 * 1000 * 1000):F2}Gb, free space {storage.Free / (1000 * 1000 * 1000):F2}Gb, speed {storage.Speed / (1000*1000):F2}MBytes/sec");
                        }
                    }

                // Run Macro Fie
                // This is only handled here if RepRapFirmware is not performing any system macros
                case 98:
                    if (!code.IsFromSystemMacro)
                    {
                        CodeParameter pParam = code.Parameter('P');
                        if (pParam != null)
                        {
                            string path = await FilePath.ToPhysical(pParam, "sys");
                            if (File.Exists(path))
                            {
                                MacroFile macro = new MacroFile(path, code.Channel, false, code.SourceConnection);
                                return await macro.RunMacro();
                            }
                            else
                            {
                                path = await FilePath.ToPhysical(pParam, "macros");
                                if (File.Exists(path))
                                {
                                    MacroFile macro = new MacroFile(path, code.Channel, false, code.SourceConnection);
                                    return await macro.RunMacro();
                                }
                                else
                                {
                                    return new CodeResult(DuetAPI.MessageType.Error, $"Could not file macro file {pParam}");
                                }
                            }
                        }
                        return new CodeResult();
                    }
                    break;

                // Return from macro
                // This is only handled here if RepRapFirmware is not performing any system macros
                case 99:
                    if (!code.IsFromSystemMacro)
                    {
                        if (!MacroFile.AbortLastFile(code.Channel))
                        {
                            return new CodeResult(DuetAPI.MessageType.Error, "Not executing a macro file");
                        }
                        return new CodeResult();
                    }
                    break;

                // Emergency Stop
                case 112:
                    await SPI.Interface.RequestEmergencyStop();
                    using (await Model.Provider.AccessReadWrite())
                    {
                        Model.Provider.Get.State.Status = MachineStatus.Halted;
                    }
                    return new CodeResult();

                // Save heightmap
                case 374:
                    {
                        string file = code.Parameter('P', "heightmap.csv");

                        try
                        {
                            Heightmap map = await SPI.Interface.GetHeightmap();
                            await map.Save(await FilePath.ToPhysical(file, "sys"));
                            return new CodeResult(DuetAPI.MessageType.Success, $"Height map saved to file {file}");
                        }
                        catch (AggregateException ae)
                        {
                            return new CodeResult(DuetAPI.MessageType.Error, $"Failed to save height map to file {file}: {ae.InnerException.Message}");
                        }
                    }

                // Load heightmap
                case 375:
                    {
                        string file = await FilePath.ToPhysical(code.Parameter('P', "heightmap.csv"), "sys");

                        try
                        {
                            Heightmap map = new Heightmap();
                            await map.Load(file);
                            await SPI.Interface.SetHeightmap(map);
                            return new CodeResult();
                        }
                        catch (AggregateException ae)
                        {
                            return new CodeResult(DuetAPI.MessageType.Error, $"Failed to load height map from file {file}: {ae.InnerException.Message}");
                        }
                    }

                // Create Directory on SD-Card
                case 470:
                    {
                        string path = code.Parameter('P', "");
                        try
                        {
                            Directory.CreateDirectory(await FilePath.ToPhysical(path));
                            return new CodeResult();
                        }
                        catch (Exception e)
                        {
                            return new CodeResult(MessageType.Error, $"Failed to create directory {path}: {e.Message}");
                        }
                    }

                // Rename File/Directory on SD-Card
                case 471:
                    {
                        string from = code.Parameter('S');
                        string to = code.Parameter('T');

                        try
                        {
                            string source = await FilePath.ToPhysical(from);
                            string destination = await FilePath.ToPhysical(to);

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

                // Store parameters
                case 500:
                    await Utility.ConfigOverride.Save(code);
                    break;

                // Print settings
                case 503:
                    {
                        string configFile = await FilePath.ToPhysical(MacroFile.ConfigFile, "sys");
                        if (File.Exists(configFile))
                        {
                            string content = await File.ReadAllTextAsync(configFile);
                            return new CodeResult(MessageType.Success, content);
                        }
                        return new CodeResult(MessageType.Error, "Configuration file not found");
                    }

                // Reset controller
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
                    using (await Model.Provider.AccessReadWrite())
                    {
                        Model.Provider.Get.Channels[code.Channel].RelativeExtrusion = false;
                    }
                    break;

                // Relative extrusion
                case 83:
                    using (await Model.Provider.AccessReadWrite())
                    {
                        Model.Provider.Get.Channels[code.Channel].RelativeExtrusion = false;
                    }
                    break;

                // Diagnostics
                case 122:
                    SPI.Interface.Diagnostics(result);
                    break;
            }
            return result;
        }
    }
}
