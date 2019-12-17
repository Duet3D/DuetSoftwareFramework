using System;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.Utility;

namespace DuetControlServer.Codes
{
    /// <summary>
    /// Static class that processes G-codes in the control server
    /// </summary>
    public static class GCodes
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Process a G-code that should be interpreted by the control server
        /// </summary>
        /// <param name="code">Code to process</param>
        /// <returns>Result of the code if the code completed, else null</returns>
        public static async Task<CodeResult> Process(Code code)
        {
            switch (code.MajorNumber)
            {
                // Save or load heightmap
                case 29:
                    CodeParameter cp = code.Parameter('S', 0);
                    if (cp == 1 || cp == 3)
                    {
                        if (await SPI.Interface.Flush(code.Channel))
                        {
                            string file = code.Parameter('P', FilePath.DefaultHeightmapFile);
                            string physicalFile = await FilePath.ToPhysicalAsync(file, FileDirectory.System);

                            try
                            {
                                Heightmap map = null;
                                if (cp == 1)
                                {
                                    map = new Heightmap();
                                    await map.Load(physicalFile);
                                }
                                if (await SPI.Interface.LockMovementAndWaitForStandstill(code.Channel))
                                {
                                    if (cp == 1)
                                    {
                                        await SPI.Interface.SetHeightmap(map);
                                        await SPI.Interface.UnlockAll(code.Channel);
                                        using (await Model.Provider.AccessReadWriteAsync())
                                        {
                                            Model.Provider.Get.Move.HeightmapFile = await FilePath.ToVirtualAsync(physicalFile);
                                        }
                                        return new CodeResult(DuetAPI.MessageType.Success, $"Height map loaded from file {file}");
                                    }
                                    else
                                    {
                                        map = await SPI.Interface.GetHeightmap();
                                        await SPI.Interface.UnlockAll(code.Channel);
                                        if (map.NumX * map.NumY > 0)
                                        {
                                            await map.Save(physicalFile);
                                            using (await Model.Provider.AccessReadWriteAsync())
                                            {
                                                Model.Provider.Get.Move.HeightmapFile = await FilePath.ToVirtualAsync(physicalFile);
                                            }
                                            return new CodeResult(DuetAPI.MessageType.Success, $"Height map saved to file {file}");
                                        }
                                        return new CodeResult();
                                    }
                                }
                            }
                            catch (Exception e)
                            {
                                _logger.Debug(e, "Failed to access height map file");
                                if (e is AggregateException ae)
                                {
                                    e = ae.InnerException;
                                }
                                return new CodeResult(DuetAPI.MessageType.Error, $"Failed to {(cp == 1 ? "load" : "save")} height map {(cp == 1 ? "from" : "to")} file {file}: {e.Message}");
                            }
                        }
                        throw new OperationCanceledException();
                    }
                    break;
            }
            return null;
        }

        /// <summary>
        /// React to an executed G-code before its result is returend
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
                // Rapid/Regular positioning
                case 0:
                case 1:
                    CodeParameter feedrate = code.Parameter('F');
                    if (feedrate != null)
                    {
                        using (await Model.Provider.AccessReadWriteAsync())
                        {
                            if (Model.Provider.Get.Channels[code.Channel].UsingInches)
                            {
                                Model.Provider.Get.Channels[code.Channel].Feedrate = feedrate / 25.4F;
                            }
                            else
                            {
                                Model.Provider.Get.Channels[code.Channel].Feedrate = feedrate;
                            }
                        }
                    }
                    break;

                // Use inches
                case 20:
                    using (await Model.Provider.AccessReadWriteAsync())
                    {
                        Model.Provider.Get.Channels[code.Channel].UsingInches = true;
                    }
                    break;

                // Use millimetres
                case 21:
                    using (await Model.Provider.AccessReadWriteAsync())
                    {
                        Model.Provider.Get.Channels[code.Channel].UsingInches = false;
                    }
                    break;

                // Save heightmap
                case 29:
                    if (code.Parameter('S', 0) == 0)
                    {
                        string file = code.Parameter('P', FilePath.DefaultHeightmapFile);
                        string physicalFile = await FilePath.ToPhysicalAsync(file, FileDirectory.System);

                        try
                        {
                            if (await SPI.Interface.LockMovementAndWaitForStandstill(code.Channel))
                            {
                                Heightmap map = await SPI.Interface.GetHeightmap();
                                await SPI.Interface.UnlockAll(code.Channel);

                                if (map.NumX * map.NumY > 0)
                                {
                                    await map.Save(physicalFile);
                                    using (await Model.Provider.AccessReadWriteAsync())
                                    {
                                        Model.Provider.Get.Move.HeightmapFile = await FilePath.ToVirtualAsync(physicalFile);
                                    }
                                    code.Result.Add(DuetAPI.MessageType.Success, $"Height map saved to file {file}");
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            _logger.Debug(e, "Failed to access height map file");
                            if (e is AggregateException ae)
                            {
                                e = ae.InnerException;
                            }
                            code.Result.Add(DuetAPI.MessageType.Error, $"Failed to save height map to file {file}: {e.Message}");
                        }
                    }
                    break;

                // Absolute positioning
                case 90:
                    using (await Model.Provider.AccessReadWriteAsync())
                    {
                        Model.Provider.Get.Channels[code.Channel].RelativePositioning = false;
                    }
                    break;

                // Relative positioning
                case 91:
                    using (await Model.Provider.AccessReadWriteAsync())
                    {
                        Model.Provider.Get.Channels[code.Channel].RelativePositioning = true;
                    }
                    break;
            }
        }
    }
}
