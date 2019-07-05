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

        private async Task<CodeResult> ExecuteInternally()
        {
            CodeResult result = null;

            // Preprocess this code
            if (!Flags.HasFlag(CodeFlags.IsPreProcessed))
            {
                result = await Interception.Intercept(this, InterceptionMode.Pre);
                Flags |= CodeFlags.IsPreProcessed;

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
            if (!Flags.HasFlag(CodeFlags.IsPostProcessed))
            {
                result = await Interception.Intercept(this, InterceptionMode.Post);
                Flags |= CodeFlags.IsPostProcessed;

                if (result != null)
                {
                    return await OnCodeExecuted(result);
                }
            }

            // Code has not been interpreted yet
            return null;
        }

        /// <summary>
        /// Run an arbitrary G/M/T-code and wait for it to finish
        /// </summary>
        /// <returns>Code result instance</returns>
        public override async Task<CodeResult> Execute()
        {
            // Attempt to process the code internally
            CodeResult result = await ExecuteInternally();
            if (result != null)
            {
                return result;
            }

            // Send it to RepRapFirmware unless it is a comment
            if (Type != CodeType.Comment)
            {
                if (Flags.HasFlag(CodeFlags.Asynchronous))
                {
                    // Enqueue the code for execution by RRF and return no result
                    Task<CodeResult> codeTask = Interface.ProcessCode(this);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            CodeResult res = await codeTask;
                            res = await OnCodeExecuted(res);
                            await Model.Provider.Output(res);
                        }
                        catch (AggregateException ae)
                        {
                            Console.WriteLine($"[err] {this} -> {ae.InnerException.Message}");
                        }
                    });
                    return null;
                }
                else
                {
                    // Wait for the code to complete
                    result = await Interface.ProcessCode(this);
                }
            }

            return await OnCodeExecuted(result);
        }

        /// <summary>
        /// Start an arbitrary G/M/T-code and get the task that is resolved when RRF finishes its execution.
        /// This is an internal alternative to the <see cref="CodeFlags.Asynchronous"/> flag.
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public Task<CodeResult> Enqueue()
        {
            CodeResult result = ExecuteInternally().Result;
            if (result != null)
            {
                return Task.FromResult(result);
            }

            if (Type != CodeType.Comment)
            {
                Task<CodeResult> codeTask = Interface.ProcessCode(this);
                return codeTask.ContinueWith((task) => OnCodeExecuted(task.Result).Result);
            }
            return OnCodeExecuted(result);
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

            // RepRapFirmware generally prefixes error messages with the code itself.
            // Do this only for error messages that come either from a print or from a macro
            if (result != null && (Flags.HasFlag(CodeFlags.IsFromMacro) || Channel == CodeChannel.File))
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