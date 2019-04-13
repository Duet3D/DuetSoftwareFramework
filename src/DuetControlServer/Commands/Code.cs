using System;
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

            // Comments are handled before they are sent to the firmware
            if (Type == CodeType.Comment)
            {
                return new CodeResult();
            }

            // Send it to RepRapFirmware. If this code comes from a system macro, do not wait for its completion but enqueue it
            if (IsFromSystemMacro)
            {
                Task<CodeResult> onCodeComplete = Interface.ProcessSystemCode(this);
                Task backgroundTask = Task.Run(async () =>
                {
                    try
                    {
                        result = await onCodeComplete;
                        await OnCodeExecuted(result);
                        await Model.Provider.Output(result);
                    }
                    catch (AggregateException ae)
                    {
                        await Model.Provider.Output(DuetAPI.MessageType.Error, $"{ToShortString()}: {ae.InnerException.Message}");
                    }
                });
            }
            else
            {
                result = await Interface.ProcessCode(this);
                await OnCodeExecuted(result);
            }

            return result;
        }

        private async Task OnCodeExecuted(CodeResult result)
        {
            switch (Type)
            {
                case CodeType.GCode:
                    await GCodes.CodeExecuted(this, result);
                    break;

                case CodeType.MCode:
                    await MCodes.CodeExecuted(this, result);
                    break;

                case CodeType.TCode:
                    await TCodes.CodeExecuted(this, result);
                    break;
            }
        }
    }
}