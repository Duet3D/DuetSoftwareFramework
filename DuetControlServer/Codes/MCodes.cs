using System.Threading.Tasks;
using DuetAPI.Commands;

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
                        return await FileHelper.RunMacro(pParam.AsString, code);
                    }
                    return new CodeResult();
            }
            return null;
        }
    }
}
