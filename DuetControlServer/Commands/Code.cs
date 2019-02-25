using DuetAPI.Commands;
using DuetControlServer.Codes;
using System.Threading.Tasks;
using DuetAPI.Connection;

namespace DuetControlServer.Commands
{
    public class Code : DuetAPI.Commands.Code
    {
        public Code() : base() {}

        public Code(string code) : base(code) {}
        
        // Run an arbitrary G/M/T-code
        protected override async Task<CodeResult> Run()
        {
            if (Type == CodeType.Comment)
            {
                // Comments are discarded
                return new CodeResult();
            }
            CodeResult result = null;

            // Preprocess this code
            if (!IsPreProcessed)
            {
                result = await IPC.Processors.Interception.Intercept(this, InterceptionMode.Pre);
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
                result = await IPC.Processors.Interception.Intercept(this, InterceptionMode.Post);
                if (result != null)
                {
                    return result;
                }
                IsPostProcessed = true;
            }

            // Then have it processed by RepRapFirmware
            return await RepRapFirmware.Connector.ProcessCode(this);
        }
    }
}