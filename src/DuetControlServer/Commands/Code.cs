using System;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetControlServer.Codes;
using DuetControlServer.IPC.Processors;
using DuetControlServer.SPI;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.Code"/> command
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
        public Code(string code) : base(code) { }

        /// <summary>
        /// Run an arbitrary G/M/T-code
        /// </summary>
        /// <returns>Code result instance</returns>
        public override async Task<CodeResult> Execute()
        {
            CodeResult result = null;

            // Preprocess this code
            if (!IsPreProcessed)
            {
                result = await Interception.Intercept(this, InterceptionMode.Pre);
                IsPreProcessed = true;

                if (result != null)
                {
                    return await OnCodeExecuted(result);
                }
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
                return await OnCodeExecuted(result);
            }

            // If the could not be interpreted, post-process it
            if (!IsPostProcessed)
            {
                result = await Interception.Intercept(this, InterceptionMode.Post);
                IsPostProcessed = true;

                if (result != null)
                {
                    return await OnCodeExecuted(result);
                }
            }

            // Send it to RepRapFirmware unless it is a comment
            if (Type != CodeType.Comment)
            {
                result = await Interface.ProcessCode(this);
            }

            return await OnCodeExecuted(result);
        }

        private async Task<CodeResult> OnCodeExecuted(CodeResult result)
        {
            // Process code result
            switch (Type)
            {
                case CodeType.GCode:
                    result = await GCodes.CodeExecuted(this, result);
                    break;

                case CodeType.MCode:
                    result = await MCodes.CodeExecuted(this, result);
                    break;

                case CodeType.TCode:
                    result = await TCodes.CodeExecuted(this, result);
                    break;
            }

            // Make sure to return a result. If none is given, assume success
            if (result == null)
            {
                result = new CodeResult();
            }

            // RepRapFirmware generally prefixes error messages with the code itself.
            // Do this only for error messages that come either from a print or from a macro
            if (IsFromMacro || Channel == CodeChannel.File)
            {
                foreach (Message msg in result)
                {
                    if (msg.Type == MessageType.Error)
                    {
                        msg.Content = ToShortString() + ": " + msg.Content;
                    }
                }
            }

            // Finished. Optionally an "Executed" interceptor could be called here, but that would only make sense if the code reply was included
            return result;
        }
    }
}