using DuetAPI;
using DuetAPI.Commands;
using DuetControlServer.Codes;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    public class Code : DuetAPI.Commands.Code
    {
        public Code() : base() { }
        public Code(string codeString) : base(codeString) { }

        // Run an arbitrary G/M/T-code
        public override async Task<CodeResult> Execute()
        {
            if (Type == CodeType.Comment)
            {
                // Comments are discarded
                return new CodeResult(this);
            }

            CodeResult result = null;

            // Preprocess this code
            if (!IsPreProcessed)
            {
                result = await IPC.Worker.Interception.Intercept(this, InterceptionType.Pre);
                if (result != null)
                {
                    return result;
                }
                IsPreProcessed = true;
            }

            // Attempt to process the code internally
            switch (Type)
            {
                case CodeType.GCode:
                    result = await GCodes.Process(this);
                    break;

                case CodeType.MCode:
                    result = await MCodes.Process(this);
                    break;

                case CodeType.TCode:
                    result = await TCodes.Process(this);
                    break;
            }

            if (result != null)
            {
                return result;
            }

            // If the could not be interpreted, post-process it
            if (!IsPostProcessed)
            {
                result = await IPC.Worker.Interception.Intercept(this, InterceptionType.Post);
                if (result != null)
                {
                    return result;
                }
                IsPostProcessed = true;
            }

            // Then send it to RepRapFirmware
            return await RepRapFirmware.Connector.ProcessCode(this);
        }
    }
}