using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    public class SimpleCode : DuetAPI.Commands.SimpleCode
    {
        // Convert a simple code into a regular code, execute it and return its result as string
        protected override async Task<string> Run()
        {
            Code code = new Code(Code) { SourceConnection = SourceConnection };
            object result = await code.Execute();
            return result.ToString();
        }
    }
}