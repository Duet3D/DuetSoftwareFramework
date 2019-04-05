using System.IO;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetControlServer.FileExecution;

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
                    break;

                // Run Macro File
                case 98:
                    CodeParameter pParam = code.GetParameter('P');
                    if (pParam != null)
                    {
                        string path = await FilePath.ToPhysical(pParam.AsString);
                        if (File.Exists(path))
                        {
                            MacroFile macro = new MacroFile(path, code.Channel, code.SourceConnection);
                            return await macro.RunMacro();
                        }
                    }
                    return new CodeResult();

                // Return from macro
                case 99:
                    if (!MacroFile.AbortLastFile(code.Channel))
                    {
                        return new CodeResult(DuetAPI.MessageType.Error, "Not executing a macro file");
                    }
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
