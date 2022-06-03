using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;

namespace DuetControlServer.Codes.Handlers
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
        public static Task<Message> Process(Code code) => Task.FromResult<Message>(null);

        /// <summary>
        /// React to an executed T-code before its result is returned
        /// </summary>
        /// <param name="code">Code processed by RepRapFirmware</param>
        /// <returns>Result to output</returns>
        public static Task CodeExecuted(Code code) => Task.CompletedTask;
    }
}
