using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    public class SimpleCode : DuetAPI.Commands.SimpleCode
    {
        // Convert a simple code into a regular code, execute it and return its result as string
        public override async Task<string> Execute()
        {
            Code code = new Code(this.Code);
            DuetAPI.Commands.CodeResult result = await code.Execute();
            return result.ToString();
        }
    }
}