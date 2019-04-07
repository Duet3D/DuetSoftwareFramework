using System.Threading.Tasks;
using DuetAPI.Commands;

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
        public static Task<CodeResult> Process(Code code)
        {
            // nothing in here yet...
            return Task.FromResult<CodeResult>(null);
        }

        /// <summary>
        /// React to an executed G-code before its result is returend
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
                // Use inches
                case 20:
                    using (await Model.Provider.AccessReadWrite())
                    {
                        Model.Provider.Get.Channels[code.Channel].UsingInches = true;
                    }
                    break;

                // Use millimetres
                case 21:
                    using (await Model.Provider.AccessReadWrite())
                    {
                        Model.Provider.Get.Channels[code.Channel].UsingInches = false;
                    }
                    break;

                // Absolute positioning
                case 90:
                    using (await Model.Provider.AccessReadWrite())
                    {
                        Model.Provider.Get.Channels[code.Channel].RelativePositioning = false;
                    }
                    break;

                // Relative positioning
                case 91:
                    using (await Model.Provider.AccessReadWrite())
                    {
                        Model.Provider.Get.Channels[code.Channel].RelativePositioning = true;
                    }
                    break;
            }
        }
    }
}
