using DuetAPI.Commands;
using System.Threading.Tasks;
using DuetAPI;

namespace DuetControlServer.Codes
{
    public static class MCodes
    {
        // Run an M-code and return null if it could not be processed
        public static async Task<CodeResult> Process(Code code)
        {
            switch (code.MajorNumber)
            {
                // Run Macro File
                case 98:
                    CodeParameter pParam = code.GetParameter('P');
                    if (pParam != null)
                    {
                        return await File.RunMacro(pParam.AsString, code);
                    }
                    return new CodeResult();
            }
            return null;
        }
    }
}
