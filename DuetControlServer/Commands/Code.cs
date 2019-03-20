using DuetAPI.Commands;
using DuetControlServer.Codes;
using System.Threading.Tasks;
using DuetAPI.Connection;

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
        public Code() : base() {}

        /// <summary>
        /// Creates a new Code instance and attempts to parse the given code string
        /// </summary>
        /// <param name="code">G/M/T-Code</param>
        public Code(string code) : base(code) {}

        /// <summary>
        /// Internal file position when processing a file
        /// </summary>
        internal long FilePosition;

        /// <summary>
        /// Virtual extruder positions before this move
        /// </summary>
        internal float[] VirtualExtruderPositions;

        /// <summary>
        /// Diff amount of virtual extruder positions 
        /// </summary>
        internal float[] VirtualExtruderAmounts;

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
            return await SPI.Connector.ProcessCode(this);
        }
    }
}