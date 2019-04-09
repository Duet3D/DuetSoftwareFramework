using DuetAPI.Commands;
using DuetAPI.Machine;
using DuetControlServer.FileExecution;
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
        /// <summary>
        /// Process an M-code that should be interpreted by the control server
        /// </summary>
        /// <param name="code">Code to process</param>
        /// <returns>Result of the code if the code completed, else null</returns>
        public static async Task<CodeResult> Process(Code code)
        {
            switch (code.MajorNumber)
            {
                // Start a file print
                case 32:
                    string file = await FilePath.ToPhysical(code.GetUnprecedentedString());
                    await Print.Start(file);
                    return new CodeResult();

                // Run Macro Fie
                // This is only handled here if RepRapFirmware is not performing any system macros
                case 98:
                    if (!code.IsFromSystemMacro)
                    {
                        CodeParameter pParam = code.GetParameter('P');
                        if (pParam != null)
                        {
                            string path = await FilePath.ToPhysical(pParam.AsString, "sys");
                            if (File.Exists(path))
                            {
                                MacroFile macro = new MacroFile(path, code.Channel, false, code.SourceConnection);
                                return await macro.RunMacro();
                            }
                            else
                            {
                                path = await FilePath.ToPhysical(pParam.AsString, "macros");
                                if (File.Exists(path))
                                {
                                    MacroFile macro = new MacroFile(path, code.Channel, false, code.SourceConnection);
                                    return await macro.RunMacro();
                                }
                                else
                                {
                                    return new CodeResult(DuetAPI.MessageType.Error, $"Could not file macro file {pParam.AsString}");
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

                // Reset controller
                case 999:
                    await SPI.Interface.RequestReset();
                    return new CodeResult();
            }
            return null;
        }

        /// <summary>
        /// React to an executed M-code before its result is returend
        /// </summary>
        /// <param name="code">Code processed by RepRapFirmware</param>
        /// <param name="result">Result that it generated</param>
        /// <returns>Asynchronous task</returns>
        public static async Task CodeExecuted(Code code, CodeResult result)
        {
            if (!result.IsSuccessful)
            {
                return;
            }

            switch (code.MajorNumber)
            {
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
            }
        }
    }
}
