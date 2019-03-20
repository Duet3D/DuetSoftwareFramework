using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the SimpleCode command
    /// </summary>
    public class SimpleCode : DuetAPI.Commands.SimpleCode
    {
        /// <summary>
        /// Converts a simple G/M/T-code to a regular Code instance, executes it and returns its result as text
        /// </summary>
        /// <returns>G-code result</returns>
        protected override async Task<string> Run()
        {
            Code code = new Code(Code) { SourceConnection = SourceConnection };
            object result = await code.Execute();
            return result.ToString();
        }
    }
}