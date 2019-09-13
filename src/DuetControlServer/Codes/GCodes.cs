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
        /// Process a G-code that should be interpreted by the control server
        /// </summary>
        /// <param name="code">Code to process</param>
        /// <returns>Result of the code if the code completed, else null</returns>
        public static async Task<CodeResult> Process(Code code)
        {
            switch (code.MajorNumber)
            {
                // Load heightmap
                case 29:
                    if (code.Parameter('S', 0) == 1)
                    {
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
                                return new CodeResult(DuetAPI.MessageType.Error, $"Failed to load height map from file {file}: {ae.InnerException.Message}");
                            }
                        }
                        return new CodeResult();
                    }
                    break;
            }
            return null;
        }

        /// <summary>
        /// React to an executed G-code before its result is returend
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
                        if (await SPI.Interface.Flush(code.Channel))
                        {
                            string file = code.Parameter('P', "heightmap.csv");
                            try
                            {
                                if (await SPI.Interface.LockMovementAndWaitForStandstill(code.Channel))
                                {
                                    Heightmap map = await SPI.Interface.GetHeightmap();
                                    await SPI.Interface.UnlockAll(code.Channel);

                                    await map.Save(await FilePath.ToPhysicalAsync(file, "sys"));
                                    result.Add(DuetAPI.MessageType.Success, $"Height map saved to file {file}");
                                }
                            }
                            catch (AggregateException ae)
                            {
                                result.Add(DuetAPI.MessageType.Error, $"Failed to save height map to file {file}: {ae.InnerException.Message}");
                            }
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
            return result;
        }
    }
}
