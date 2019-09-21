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
        public static Task<CodeResult> Process(Code code) => Task.FromResult<CodeResult>(null);

        /// <summary>
        /// React to an executed T-code before its result is returend
        /// </summary>
        /// <param name="code">Code processed by RepRapFirmware</param>
        /// <param name="result">Result that it generated</param>
        /// <returns>Result to output</returns>
        public static async Task<CodeResult> CodeExecuted(Code code, CodeResult result)
        {
            if (!code.MajorNumber.HasValue || !result.IsSuccessful)
            {
                return result;
            }

            // Set new tool number
            using (await Model.Provider.AccessReadWriteAsync())
            {
                if (Model.Provider.Get.Tools.Any(tool => tool.Number == code.MajorNumber))
                {
                    // Make sure the chosen tool actually exists
                    Model.Provider.Get.State.CurrentTool = code.MajorNumber.Value;
                }
                else
                {
                    // Deselect the current tool if it does not exist
                    Model.Provider.Get.State.CurrentTool = -1;
                }
            }

            return result;
        }
    }
}
