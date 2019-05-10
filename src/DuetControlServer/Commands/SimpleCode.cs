using DuetAPI.Commands;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.SimpleCode"/> command
    /// </summary>
    public class SimpleCode : DuetAPI.Commands.SimpleCode
    {
        /// <summary>
        /// Converts a simple G/M/T-code to a regular Code instance, executes it and returns its result as text
        /// </summary>
        /// <returns>G-code result</returns>
        public override async Task<string> Execute()
        {
            Code code = new Code(Code) {
                Channel = Channel,
                SourceConnection = SourceConnection
            };
            CodeResult result = await code.Execute();
            return result.ToString();
        }
    }
}