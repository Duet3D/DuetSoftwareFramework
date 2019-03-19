using DuetAPI.Commands;
using System.Threading.Tasks;

namespace DuetControlServer.Codes
{
    public static class GCodes
    {
        public static async Task<CodeResult> Process(Code code)
        {
            // TODO check for G0/G1 E... / G10/G11 and adjust virtual extruder position
            // TODO
            await Task.CompletedTask;
            return null;
        }
    }
}
