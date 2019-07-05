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
        /// <returns>G-code result as text</returns>
        public override async Task<string> Execute()
        {
            Code code = new Code(Code)
            {
                Channel = Channel,
                SourceConnection = SourceConnection
            };

            // Send diagnostics request always over the Daemon channel.
            // This way, diagnotics can be output even if a code is blocking everything else
            if (code.Type == CodeType.MCode && code.MajorNumber == 122)
            {
                code.Channel = DuetAPI.CodeChannel.Daemon;
            }

            CodeResult result = await code.Execute();
            return (result != null) ? result.ToString() : "";
        }
    }
}