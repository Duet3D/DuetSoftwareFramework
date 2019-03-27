using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetControlServer.Codes;
using DuetControlServer.IPC.Processors;
using DuetControlServer.SPI;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation for G/M/T-code commands
    /// </summary>
    public class Code : DuetAPI.Commands.Code
    {
        /// <summary>
        /// Creates a new Code instance
        /// </summary>
        public Code() { }

        /// <summary>
        /// Creates a new Code instance and attempts to parse the given code string
        /// </summary>
        /// <param name="code">G/M/T-Code</param>
        public Code(string code) : base(code) {}
        
        /// <summary>
        /// Defines whether this code is part of config.g
        /// </summary>
        public bool IsFromConfig { get; set; }
        
        /// <summary>
        /// Defines whether this code is part of config-override.g
        /// </summary>
        public bool IsFromConfigOverride { get; set; }

        /// <summary>
        /// Run an arbitrary G/M/T-code
        /// </summary>
        /// <returns>Code result instance</returns>
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
                result = await Interception.Intercept(this, InterceptionMode.Pre);
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
                result = await Interception.Intercept(this, InterceptionMode.Post);
                if (result != null)
                {
                    return result;
                }
                IsPostProcessed = true;
            }

            // Then have it processed by RepRapFirmware
            return await Connector.ProcessCode(this);
        }
    }
}