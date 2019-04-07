using System;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.Commands;

namespace DuetControlServer.Codes
{
    /// <summary>
    /// Static class that processes T-codes in the control server
    /// </summary>
    public static class TCodes
    {
        /// <summary>
        /// Process a T-code that should be interpreted by the control server
        /// </summary>
        /// <param name="code">Code to process</param>
        /// <returns>Result of the code if the code completed, else null</returns>
        public static Task<CodeResult> Process(Code code)
        {
            return Task.FromResult<CodeResult>(null);
        }

        /// <summary>
        /// React to an executed T-code before its result is returend
        /// </summary>
        /// <param name="code">Code processed by RepRapFirmware</param>
        /// <param name="result">Result that it generated</param>
        /// <returns>Asynchronous task</returns>
        public static async Task CodeExecuted(Code code, CodeResult result)
        {
            if (!code.MajorNumber.HasValue || !result.IsSuccessful)
            {
                return;
            }

            // Set new tool number
            using (await Model.Provider.AccessReadWrite())
            {
                Model.Provider.Get.State.CurrentTool = Math.Max(-1, code.MajorNumber.Value);
            }
        }
    }
}
