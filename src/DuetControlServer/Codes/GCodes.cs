using System;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.Files;

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
        public static async Task<CodeResult> Process(Commands.Code code)
        {
            if (code.Channel == CodeChannel.File && FileExecution.Job.IsSimulating)
            {
                // Ignore M-codes from files in simulation mode...
                return null;
            }

            switch (code.MajorNumber)
            {
                // Save or load heightmap
                case 29:
                    CodeParameter cp = code.Parameter('S', 0);
                    if (cp == 1 || cp == 3)
                    {
                        if (await SPI.Interface.Flush(code))
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
                                else
                                {
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
                            }
                            catch (Exception e)
                            {
                                _logger.Debug(e, "Failed to access height map file");
                                if (e is AggregateException ae)
                                {
                                    e = ae.InnerException;
                                }
                                return new CodeResult(MessageType.Error, $"Failed to {(cp == 1 ? "load" : "save")} height map {(cp == 1 ? "from" : "to")} file {file}: {e.Message}");
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
                // Save heightmap
                case 29:
                    // If no S parameter is present, check for /sys/mesh.g and continue only if it does not exist
                    if (code.Parameter('S') == null)
                    {
                        string meshMacroFile = await FilePath.ToPhysicalAsync(FilePath.MeshFile, FileDirectory.System);
                        if (System.IO.File.Exists(meshMacroFile))
                        {
                            break;
                        }
                    }

                    if (code.Parameter('S', 0) == 0)
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
                                code.Result.Add(MessageType.Success, $"Height map saved to file {file}");
                            }
                        }
                        catch (Exception e)
                        {
                            _logger.Debug(e, "Failed to access height map file");
                            if (e is AggregateException ae)
                            {
                                e = ae.InnerException;
                            }
                            code.Result.Add(MessageType.Error, $"Failed to save height map to file {file}: {e.Message}");
                        }
                    }
                    break;
            }
        }
    }
}
